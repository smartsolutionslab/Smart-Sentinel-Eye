using Microsoft.Extensions.Logging;
using SmartSentinelEye.Shared.Contracts;
using SmartSentinelEye.Shared.Contracts.SystemVariables;
using SmartSentinelEye.Shared.CQRS;
using SmartSentinelEye.Shared.Kernel;
using SmartSentinelEye.SystemVariables.Application.Resolution;
using SmartSentinelEye.SystemVariables.Domain.Variable;
using SmartSentinelEye.SystemVariables.Domain.Variable.Events;

namespace SmartSentinelEye.SystemVariables.Application.EventHandlers;

/// <summary>
/// Reacts to a variable being archived: publishes the V1 event, then
/// re-resolves every affected overlay (the archived variable's
/// placeholder reverts to literal per FR-011) and publishes a
/// <see cref="ResolvedOverlayTextChangedV1"/> per overlay for
/// LayoutComposition to broadcast — same split as the value-changed
/// handler.
/// </summary>
public sealed class VariableArchivedDomainEventHandler(
    IEventBus events,
    IReverseIndex reverseIndex,
    IVariableRepository variables,
    IResolver resolver,
    ILogger<VariableArchivedDomainEventHandler> logger)
    : IDomainEventHandler<VariableArchivedDomainEvent>
{
    public async Task Handle(VariableArchivedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        Ensure.That(domainEvent).IsNotNull();

        var (variableIdentifier, fab, variableName, archivedAt, archivedBy) = domainEvent;

        SystemVariableArchivedV1 systemVariableArchivedEvent = new(
            Variable: variableIdentifier.Value,
            Name: variableName.Value,
            ArchivedAt: archivedAt,
            ArchivedBy: archivedBy.Value,
            Metadata: new EventMetadata(Guid.CreateVersion7(), archivedAt, fab.Value, archivedBy.Value));

        await events.PublishAsync(systemVariableArchivedEvent, cancellationToken);

        IReadOnlyCollection<Guid> affectedOverlays = reverseIndex.LookupOverlays(variableName.Value);
        if (affectedOverlays.Count == 0)
        {
            return;
        }

        foreach (Guid overlayId in affectedOverlays)
        {
            string? labelText = reverseIndex.LookupLabelText(overlayId);
            if (labelText is null)
            {
                continue;
            }

            // Build a snapshot of every OTHER variable in the label
            // (the archived one is intentionally absent so it
            // resolves to its literal placeholder).
            Dictionary<string, VariableSnapshotEntry> snapshot = new(StringComparer.Ordinal);
            foreach (string name in PlaceholderParser.ExtractNames(labelText))
            {
                if (string.Equals(name, variableName.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                VariableName parsed;
                try { parsed = VariableName.From(name); }
                catch (ArgumentException) { continue; }

                // Siblings resolve in the fab that changed (ADR-0115).
                Option<Variable> other = await variables.GetByNameAsync(
                    fab, parsed, cancellationToken);
                if (!other.HasValue)
                {
                    continue;
                }

                Variable variable = other.Value;
                if (variable.State == VariableState.Archived)
                {
                    continue;
                }

                if (variable.Value is VariableValue.Unset)
                {
                    continue;
                }

                snapshot[name] = new VariableSnapshotEntry(variable.Value, variable.BooleanLabels);
            }

            string resolvedText = resolver.Resolve(labelText, snapshot);
            long version = reverseIndex.NextVersionFor(overlayId);

            ResolvedOverlayTextChangedV1 resolvedOverlayTextChangedEvent = new(
                Overlay: overlayId,
                ResolvedText: resolvedText,
                Version: version,
                Metadata: new(
                    Guid.CreateVersion7(),
                    archivedAt,
                    fab.Value,
                    archivedBy.Value));
            await events.PublishAsync(resolvedOverlayTextChangedEvent, cancellationToken);
        }

        logger.PushedResolvedTextAfterArchive(affectedOverlays.Count, variableName);
    }
}
