using InterviewScribe.Core.Domain;
using InterviewScribe.Infrastructure.Pipeline;

namespace InterviewScribe.Tests;

public sealed class PipelineProgressCoordinatorTests
{
    [Fact]
    public void FastMode_MapsLocalStagesToMonotonicOverallProgress()
    {
        var snapshots = new List<OperationProgress>();
        var coordinator = new PipelineProgressCoordinator(
            TranscriptionMode.MossLocalFast,
            new CollectingProgress(snapshots));

        coordinator.Report(PipelinePhase.PrepareModel, JobState.VerifyingModel, "model", 1);
        coordinator.Report(PipelinePhase.ExtractAudio, JobState.ExtractingAudio, "audio", 0);
        coordinator.Report(PipelinePhase.ExtractAudio, JobState.ExtractingAudio, "audio", 0.5);
        coordinator.Report(PipelinePhase.MossDiarization, JobState.Transcribing, "gpu", 0.5);
        coordinator.ResetEta();
        coordinator.Report(PipelinePhase.MossDiarization, JobState.Transcribing, "cpu retry", 0);
        coordinator.Report(PipelinePhase.Validate, JobState.ValidatingResult, "validate", 0);
        coordinator.Report(PipelinePhase.Completed, JobState.Completed, "done", 1);

        Assert.All(snapshots, value =>
        {
            Assert.NotNull(value.Fraction);
            Assert.True(double.IsFinite(value.Fraction!.Value));
            Assert.InRange(value.Fraction.Value, 0, 1);
        });
        Assert.True(snapshots.Zip(snapshots.Skip(1), (left, right) => left.Fraction <= right.Fraction).All(value => value));
        Assert.Equal(0.10, snapshots[0].Fraction!.Value, 3);
        Assert.Equal(snapshots[3].Fraction, snapshots[4].Fraction);
        Assert.Equal(1, snapshots[^1].Fraction);
    }

    [Fact]
    public void HighAccuracyMode_KeepsMossAndQwenInSeparateRanges()
    {
        var snapshots = new List<OperationProgress>();
        var coordinator = new PipelineProgressCoordinator(
            TranscriptionMode.QwenLocalHighAccuracy,
            new CollectingProgress(snapshots));

        coordinator.Report(PipelinePhase.MossDiarization, JobState.Transcribing, "moss", 1);
        coordinator.Report(PipelinePhase.QwenRecognition, JobState.Transcribing, "qwen start", 0);
        coordinator.Report(PipelinePhase.QwenRecognition, JobState.Transcribing, "qwen half", 0.5);
        coordinator.Report(PipelinePhase.QwenRecognition, JobState.Transcribing, "qwen done", 1);

        Assert.Equal(0.48, snapshots[0].Fraction!.Value, 3);
        Assert.Equal(0.48, snapshots[1].Fraction!.Value, 3);
        Assert.Equal(0.71, snapshots[2].Fraction!.Value, 3);
        Assert.Equal(0.94, snapshots[3].Fraction!.Value, 3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidChildFraction_NeverReachesUi(double invalidFraction)
    {
        var snapshots = new List<OperationProgress>();
        var coordinator = new PipelineProgressCoordinator(
            TranscriptionMode.MossLocalFast,
            new CollectingProgress(snapshots));

        coordinator.Report(PipelinePhase.ExtractAudio, JobState.ExtractingAudio, "invalid", invalidFraction);

        var snapshot = Assert.Single(snapshots);
        Assert.Null(snapshot.PhaseFraction);
        Assert.True(double.IsFinite(snapshot.Fraction!.Value));
        Assert.Equal(0.10, snapshot.Fraction.Value, 3);
    }

    private sealed class CollectingProgress(List<OperationProgress> values) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => values.Add(value);
    }
}
