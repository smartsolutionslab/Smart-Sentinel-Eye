using System.Collections.Concurrent;
using SmartSentinelEye.Automation.Application.Evaluation;
using SmartSentinelEye.Automation.Domain.Rule;
using RuleAggregate = SmartSentinelEye.Automation.Domain.Rule.Rule;
using SmartSentinelEye.Shared.Kernel;

namespace SmartSentinelEye.Automation.Application.Tests.Fakes;

/// <summary>
/// Test-side cache that mirrors the production
/// <c>Automation.Infrastructure.Cache.InMemoryRuleCache</c> but
/// without DI / hosted-service plumbing. Stores rules by
/// <c>(fab, source, kind)</c> and exposes them in <c>CreatedAt</c> ascending
/// order so the last-write-wins fan-out (FR-012) is deterministic.
///
/// <para>
/// The fab must be part of the key here exactly as it is in production. A
/// fake that keyed on the trigger alone would return another fab's rules and
/// every evaluator test would still pass — which is the shape of the bug
/// being fixed, reproduced in the thing meant to detect it.
/// </para>
///
/// <para>
/// <b>And that is exactly why the answer alone proves nothing about the
/// caller</b> (#2151, census detection 4). Because the fab is part of the key,
/// the filter under test lives in this class rather than in
/// <c>RuleEvaluator</c>, which does no fab filtering of its own: a caller that
/// hard-codes the fab it looks up, or that falls back to some other fab when
/// the event carries none, simply misses the bucket and the absence-assertions
/// downstream stay green. The key the caller *asked for* is the one thing the
/// caller chooses, so <see cref="Lookups"/> records it — which is what turns
/// this from a keyed double into the recording kind the other 71
/// absence-assertions in this repository use.
/// </para>
/// </summary>
public sealed class InMemoryRuleCache : IRuleCache
{
    private readonly ConcurrentDictionary<(string Fab, string TriggerSource, string TriggerKind), List<CompiledRule>> _byTrigger = new();
    private readonly object _gate = new();
    private readonly List<(string Fab, string TriggerSource, string TriggerKind)> lookups = [];

    /// <summary>
    /// Every key <see cref="LookupActive"/> was asked for, in order — not the
    /// rules it answered with. An empty list means the caller never asked at
    /// all, which is the only representation "it evaluated nothing" has at this
    /// seam.
    /// </summary>
    public IReadOnlyList<(string Fab, string TriggerSource, string TriggerKind)> Lookups
    {
        get
        {
            lock (_gate)
            {
                return lookups.ToArray();
            }
        }
    }

    public IReadOnlyList<CompiledRule> LookupActive(
        FabIdentifier fab, string triggerSource, string triggerKind)
    {
        Ensure.That(fab).IsNotNull();

        lock (_gate)
        {
            lookups.Add((fab.Value, triggerSource, triggerKind));
        }

        if (!_byTrigger.TryGetValue((fab.Value, triggerSource, triggerKind), out List<CompiledRule>? bucket))
        {
            return Array.Empty<CompiledRule>();
        }
        lock (_gate)
        {
            return bucket.ToArray();
        }
    }

    public void Upsert(RuleAggregate rule)
    {
        Ensure.That(rule).IsNotNull();
        if (rule.State != RuleState.Active)
        {
            return;
        }

        CompiledRule compiled = CompiledRule.From(rule);
        (string Fab, string TriggerSource, string TriggerKind) key =
            (rule.Fab.Value, rule.TriggerSource.Value, rule.TriggerKind.Value);

        List<CompiledRule> bucket = _byTrigger.GetOrAdd(key, _ => []);
        lock (_gate)
        {
            bucket.RemoveAll(c => c.Identifier == rule.Id);
            bucket.Add(compiled);
            bucket.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        }
    }

    public void Remove(RuleIdentifier rule)
    {
        lock (_gate)
        {
            foreach (List<CompiledRule> bucket in _byTrigger.Values)
            {
                bucket.RemoveAll(c => c.Identifier == rule);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byTrigger.Values.Sum(b => b.Count);
            }
        }
    }
}
