using System.Diagnostics;
using InterviewScribe.Core.Domain;

namespace InterviewScribe.Infrastructure.Pipeline;

internal sealed class PipelineProgressCoordinator
{
    private static readonly TimeSpan EtaWarmup = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumEta = TimeSpan.FromDays(2);

    private readonly object _gate = new();
    private readonly TranscriptionMode _mode;
    private readonly IProgress<OperationProgress>? _target;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _lastOverallFraction;
    private PipelinePhase _lastPhase;
    private double? _phaseStartOverall;
    private TimeSpan _phaseStartElapsed;
    private double? _smoothedEtaSeconds;

    public PipelineProgressCoordinator(
        TranscriptionMode mode,
        IProgress<OperationProgress>? target)
    {
        _mode = mode;
        _target = target;
    }

    public IProgress<OperationProgress> ForPhase(PipelinePhase phase) =>
        new DelegatingProgress(value => Report(
            phase,
            value.State,
            value.Message,
            value.Fraction));

    public void Report(
        PipelinePhase phase,
        JobState state,
        string message,
        double? phaseFraction = null)
    {
        OperationProgress snapshot;
        lock (_gate)
        {
            var elapsed = _stopwatch.Elapsed;
            var normalizedPhaseFraction = NormalizeFraction(phaseFraction);
            var (start, end) = GetRange(phase);
            var mapped = normalizedPhaseFraction is double local
                ? start + ((end - start) * local)
                : start;
            var overall = phase == PipelinePhase.Completed
                ? 1d
                : Math.Min(0.999d, Math.Max(_lastOverallFraction, mapped));

            if (phase != _lastPhase)
            {
                _lastPhase = phase;
                _phaseStartOverall = overall;
                _phaseStartElapsed = elapsed;
                _smoothedEtaSeconds = null;
            }

            _lastOverallFraction = overall;
            var eta = EstimateRemaining(overall, elapsed);
            snapshot = new OperationProgress(
                state,
                message,
                overall,
                elapsed,
                eta,
                phase,
                normalizedPhaseFraction);
        }

        _target?.Report(snapshot);
    }

    public void ResetEta()
    {
        lock (_gate)
        {
            _phaseStartOverall = _lastOverallFraction;
            _phaseStartElapsed = _stopwatch.Elapsed;
            _smoothedEtaSeconds = null;
        }
    }

    private TimeSpan? EstimateRemaining(double overall, TimeSpan elapsed)
    {
        if (overall >= 1)
        {
            return TimeSpan.Zero;
        }

        if (elapsed - _phaseStartElapsed < EtaWarmup ||
            _phaseStartOverall is not double phaseStart ||
            overall - phaseStart < 0.01)
        {
            return null;
        }

        // Estimate from work completed since the current phase/retry baseline.
        // ResetEta moves both baselines, so a failed GPU attempt does not pollute
        // the CPU retry estimate while elapsed time shown to the user stays global.
        var effectiveElapsedSeconds = Math.Max(
            0.001,
            (elapsed - _phaseStartElapsed).TotalSeconds);
        var completedSinceBaseline = Math.Max(overall - phaseStart, 0.001);
        var rawSeconds = effectiveElapsedSeconds * (1d - overall) / completedSinceBaseline;
        if (!double.IsFinite(rawSeconds) || rawSeconds < 0 || rawSeconds > MaximumEta.TotalSeconds)
        {
            return null;
        }

        _smoothedEtaSeconds = _smoothedEtaSeconds is double previous
            ? (previous * 0.75) + (rawSeconds * 0.25)
            : rawSeconds;
        return TimeSpan.FromSeconds(Math.Max(1, _smoothedEtaSeconds.Value));
    }

    private (double Start, double End) GetRange(PipelinePhase phase)
    {
        var highAccuracy = _mode != TranscriptionMode.MossLocalFast;
        return phase switch
        {
            PipelinePhase.None => (0, 0),
            PipelinePhase.ProbeMedia => (0, 0.03),
            PipelinePhase.PrepareModel => (0.03, 0.10),
            PipelinePhase.ExtractAudio => (0.10, 0.20),
            PipelinePhase.MossDiarization => highAccuracy ? (0.20, 0.48) : (0.20, 0.94),
            PipelinePhase.QwenRecognition => highAccuracy ? (0.48, 0.94) : (0.94, 0.94),
            PipelinePhase.Validate => (0.94, 0.97),
            PipelinePhase.Export => (0.97, 0.999),
            PipelinePhase.Completed => (1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
    }

    private static double? NormalizeFraction(double? value) =>
        value is double fraction && double.IsFinite(fraction)
            ? Math.Clamp(fraction, 0, 1)
            : null;

    private sealed class DelegatingProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            report(value);
        }
    }
}
