using System.Text.Json;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.CaptureLogs.Comparison;

public sealed class TelemetryComparator(TimeProvider timeProvider) : ITelemetryComparator
{
    private const string ComparatorId = "wotbtreader.exact-id-window";
    private const string ComparatorVersion = "1.0.0-alpha";
    private const string SchemaVersion = "1";

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public ValueTask<OperationResult<TelemetryComparison>> CompareAsync(
        SourceArtifactId leftSourceArtifactId,
        IReadOnlyList<TelemetryEvent> left,
        SourceArtifactId rightSourceArtifactId,
        IReadOnlyList<TelemetryEvent> right,
        ComparisonOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(options);
        if (options.TimestampWindow < TimeSpan.Zero)
        {
            return ValueTask.FromResult(OperationResult.Failure<TelemetryComparison>(
                new ApplicationError("comparison.window.invalid", "Timestamp window cannot be negative.")));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ComparisonRun run = new(
            ComparisonRunId.New(),
            leftSourceArtifactId,
            rightSourceArtifactId,
            ComparatorId,
            ComparatorVersion,
            SchemaVersion,
            options.TimestampWindow,
            _timeProvider.GetUtcNow());
        List<ComparisonItem> items = [];
        HashSet<int> matchedRight = [];
        long itemSequence = 0;

        foreach (TelemetryEvent leftEvent in left.OrderBy(static item => item.SourceSequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasComparableIdentity(leftEvent) || !HasComparableTime(leftEvent))
            {
                items.Add(CreateItem(
                    run.Id,
                    ++itemSequence,
                    ComparisonClassification.Uncomparable,
                    leftEvent,
                    rightEvent: null,
                    Field: null,
                    Explanation: "Left event lacks explicit identity or comparable time evidence."));
                continue;
            }

            (int Index, TelemetryEvent Event, TimeSpan Delta)? match = right
                .Select((telemetryEvent, index) => (Index: index, Event: telemetryEvent))
                .Where(candidate => !matchedRight.Contains(candidate.Index))
                .Where(candidate => string.Equals(
                    leftEvent.EventType,
                    candidate.Event.EventType,
                    StringComparison.Ordinal))
                .Where(candidate => IdentityMatches(leftEvent, candidate.Event))
                .Select(candidate =>
                {
                    bool comparable = TryGetTimeDelta(leftEvent, candidate.Event, out TimeSpan delta);
                    return (candidate.Index, candidate.Event, Comparable: comparable, Delta: delta);
                })
                .Where(candidate => candidate.Comparable && candidate.Delta <= options.TimestampWindow)
                .OrderBy(static candidate => candidate.Delta)
                .ThenBy(static candidate => candidate.Event.SourceSequence)
                .Select(static candidate =>
                    ((int Index, TelemetryEvent Event, TimeSpan Delta)?)(
                        candidate.Index,
                        candidate.Event,
                        candidate.Delta))
                .FirstOrDefault();

            if (match is null)
            {
                items.Add(CreateItem(
                    run.Id,
                    ++itemSequence,
                    ComparisonClassification.Missing,
                    leftEvent,
                    rightEvent: null,
                    Field: null,
                    Explanation: "No right-side event matched identity, type, and timestamp window."));
                continue;
            }

            matchedRight.Add(match.Value.Index);
            AddValueComparisons(
                run.Id,
                leftEvent,
                match.Value.Event,
                match.Value.Delta,
                options,
                items,
                ref itemSequence);
        }

        foreach ((TelemetryEvent rightEvent, int index) in right
                     .Select((telemetryEvent, index) => (telemetryEvent, index))
                     .OrderBy(static item => item.telemetryEvent.SourceSequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchedRight.Contains(index))
            {
                continue;
            }

            ComparisonClassification classification =
                HasComparableIdentity(rightEvent) && HasComparableTime(rightEvent)
                    ? ComparisonClassification.Extra
                    : ComparisonClassification.Uncomparable;
            items.Add(CreateItem(
                run.Id,
                ++itemSequence,
                classification,
                leftEvent: null,
                rightEvent,
                Field: null,
                Explanation: classification == ComparisonClassification.Extra
                    ? "Right-side event had no matching left-side event."
                    : "Right event lacks explicit identity or comparable time evidence."));
        }

        ComparisonSummary summary = new(
            Exact: items.Count(static item => item.Classification == ComparisonClassification.Exact),
            Tolerant: items.Count(static item => item.Classification == ComparisonClassification.Tolerant),
            Mismatch: items.Count(static item => item.Classification == ComparisonClassification.Mismatch),
            Missing: items.Count(static item => item.Classification == ComparisonClassification.Missing),
            Extra: items.Count(static item => item.Classification == ComparisonClassification.Extra),
            Uncomparable: items.Count(static item => item.Classification == ComparisonClassification.Uncomparable));
        return ValueTask.FromResult(OperationResult.Success(new TelemetryComparison(run, summary, items)));
    }

    private static void AddValueComparisons(
        ComparisonRunId runId,
        TelemetryEvent left,
        TelemetryEvent right,
        TimeSpan timeDelta,
        ComparisonOptions options,
        List<ComparisonItem> output,
        ref long sequence)
    {
        try
        {
            using JsonDocument leftDocument = JsonDocument.Parse(left.ValuesJson);
            using JsonDocument rightDocument = JsonDocument.Parse(right.ValuesJson);
            if (leftDocument.RootElement.ValueKind != JsonValueKind.Object ||
                rightDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                output.Add(CreateItem(
                    runId,
                    ++sequence,
                    ComparisonClassification.Uncomparable,
                    left,
                    right,
                    Field: "values",
                    Explanation: "Values are not comparable JSON objects."));
                return;
            }

            Dictionary<string, JsonElement> leftFields = leftDocument.RootElement
                .EnumerateObject()
                .ToDictionary(
                    static property => property.Name,
                    static property => property.Value,
                    StringComparer.Ordinal);
            Dictionary<string, JsonElement> rightFields = rightDocument.RootElement
                .EnumerateObject()
                .ToDictionary(
                    static property => property.Name,
                    static property => property.Value,
                    StringComparer.Ordinal);
            bool emittedDifference = false;

            foreach (string field in leftFields.Keys
                         .Concat(rightFields.Keys)
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
            {
                bool hasLeft = leftFields.TryGetValue(field, out JsonElement leftValue);
                bool hasRight = rightFields.TryGetValue(field, out JsonElement rightValue);
                if (!hasLeft || !hasRight)
                {
                    emittedDifference = true;
                    output.Add(CreateItem(
                        runId,
                        ++sequence,
                        ComparisonClassification.Mismatch,
                        left,
                        right,
                        field,
                        hasLeft ? leftValue.GetRawText() : null,
                        hasRight ? rightValue.GetRawText() : null,
                        "Field exists on only one side."));
                    continue;
                }

                if (JsonElement.DeepEquals(leftValue, rightValue))
                {
                    continue;
                }

                emittedDifference = true;
                ComparisonClassification classification = IsWithinTolerance(
                    field,
                    leftValue,
                    rightValue,
                    options.FieldTolerances)
                    ? ComparisonClassification.Tolerant
                    : ComparisonClassification.Mismatch;
                output.Add(CreateItem(
                    runId,
                    ++sequence,
                    classification,
                    left,
                    right,
                    field,
                    leftValue.GetRawText(),
                    rightValue.GetRawText(),
                    classification == ComparisonClassification.Tolerant
                        ? "Numeric values differ within the configured field tolerance."
                        : "Field values differ."));
            }

            if (!emittedDifference)
            {
                ComparisonClassification classification = timeDelta == TimeSpan.Zero
                    ? ComparisonClassification.Exact
                    : ComparisonClassification.Tolerant;
                output.Add(CreateItem(
                    runId,
                    ++sequence,
                    classification,
                    left,
                    right,
                    classification == ComparisonClassification.Tolerant ? "$timestamp" : "$event",
                    Explanation: classification == ComparisonClassification.Exact
                        ? "Identity, timestamp, and values match exactly."
                        : "Identity and values match; timestamp is inside the configured window."));
            }
        }
        catch (JsonException)
        {
            output.Add(CreateItem(
                runId,
                ++sequence,
                ComparisonClassification.Uncomparable,
                left,
                right,
                Field: "values",
                Explanation: "At least one values payload contains malformed JSON."));
        }
    }

    private static bool IsWithinTolerance(
        string field,
        JsonElement left,
        JsonElement right,
        IReadOnlyDictionary<string, double> tolerances) =>
        tolerances.TryGetValue(field, out double tolerance) &&
        double.IsFinite(tolerance) &&
        tolerance >= 0 &&
        left.TryGetDouble(out double leftNumber) &&
        right.TryGetDouble(out double rightNumber) &&
        double.IsFinite(leftNumber) &&
        double.IsFinite(rightNumber) &&
        Math.Abs(leftNumber - rightNumber) <= tolerance;

    private static bool HasComparableIdentity(TelemetryEvent telemetryEvent) =>
        telemetryEvent.EntityId is not null ||
        !string.IsNullOrWhiteSpace(telemetryEvent.ParticipantIdentity);

    private static bool HasComparableTime(TelemetryEvent telemetryEvent) =>
        telemetryEvent.ReplayTime is not null || telemetryEvent.SourceTimeUtc is not null;

    private static bool IdentityMatches(TelemetryEvent left, TelemetryEvent right)
    {
        if (left.EntityId is not null || right.EntityId is not null)
        {
            return left.EntityId is not null &&
                   right.EntityId is not null &&
                   left.EntityId == right.EntityId;
        }

        return !string.IsNullOrWhiteSpace(left.ParticipantIdentity) &&
               string.Equals(
                   left.ParticipantIdentity,
                   right.ParticipantIdentity,
                   StringComparison.Ordinal);
    }

    private static bool TryGetTimeDelta(
        TelemetryEvent left,
        TelemetryEvent right,
        out TimeSpan delta)
    {
        if (left.ReplayTime is { } leftReplay && right.ReplayTime is { } rightReplay)
        {
            delta = (leftReplay - rightReplay).Duration();
            return true;
        }

        if (left.SourceTimeUtc is { } leftSource && right.SourceTimeUtc is { } rightSource)
        {
            delta = (leftSource - rightSource).Duration();
            return true;
        }

        delta = default;
        return false;
    }

    private static ComparisonItem CreateItem(
        ComparisonRunId runId,
        long sequence,
        ComparisonClassification classification,
        TelemetryEvent? leftEvent,
        TelemetryEvent? rightEvent,
        string? Field,
        string Explanation) =>
        CreateItem(
            runId,
            sequence,
            classification,
            leftEvent,
            rightEvent,
            Field,
            LeftValue: null,
            RightValue: null,
            Explanation);

    private static ComparisonItem CreateItem(
        ComparisonRunId runId,
        long sequence,
        ComparisonClassification classification,
        TelemetryEvent? leftEvent,
        TelemetryEvent? rightEvent,
        string? Field,
        string? LeftValue,
        string? RightValue,
        string Explanation) =>
        new(
            ComparisonItemId.New(),
            runId,
            sequence,
            classification,
            leftEvent?.EventType ?? rightEvent?.EventType ?? "unknown",
            leftEvent?.ReplayTime,
            rightEvent?.ReplayTime,
            leftEvent?.ParticipantIdentity ?? rightEvent?.ParticipantIdentity,
            Field,
            LeftValue,
            RightValue,
            Explanation);
}
