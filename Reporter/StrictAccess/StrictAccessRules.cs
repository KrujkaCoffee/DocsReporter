namespace DocsApi.Reporter.StrictAccess;

// These are reporting decisions, not a claim of full native DOCs ACL equivalence.
// Unknown is retained for audit, but never grants data access.
public enum StrictOutcome { Allowed, Denied, Indeterminate }
public sealed record StrictDecision(StrictOutcome Outcome, string ReasonCode, long? AccessObjectId = null)
{
    public bool Allowed => Outcome == StrictOutcome.Allowed;
    public static StrictDecision Unknown(string reason, long? area = null) => new(StrictOutcome.Indeterminate, reason, area);
    public static StrictDecision Deny(string reason, long? area = null) => new(StrictOutcome.Denied, reason, area);
}
public sealed record StrictAclRow(int AccessTypeId, int AccessGroupId, long UserId, int ReferenceId,
    long ObjectId, int StageId, int LinkTypeId, long LinkId, int Direction,
    int XAuthorId, int XReferenceId, long XObjectId, long XAccessObjectId, int XReferenceAuthorGroupId);
public sealed record StrictAccessGroup(int TypeId, IReadOnlyDictionary<int, bool?> Commands);
public sealed record StrictAclSnapshot(bool Complete, IReadOnlyList<StrictAclRow> Rows,
    IReadOnlyDictionary<int, StrictAccessGroup> Groups);
public sealed record StrictResource(int ReferenceId, long ObjectId, long AccessObjectId, bool Current,
    int State, int AccessLevel, long StageId, bool MandatoryAccess);

public static class StrictAccessRules
{
    // Supported subset: explicit object/area assignments with an explicitly verified read command.
    // No command number or group caption is treated as proof of read semantics by default.
    public static StrictDecision Evaluate(StrictResource resource, IReadOnlySet<long> principals,
        bool identityVerified, StrictAclSnapshot snapshot, int? verifiedReadCommandId = null)
    {
        if (!identityVerified || principals.Count == 0) return StrictDecision.Deny("identity_unverified");
        if (verifiedReadCommandId is null or <= 0) return StrictDecision.Unknown("read_profile_unverified");
        if (resource.ReferenceId <= 0 || resource.ObjectId <= 0) return StrictDecision.Deny("invalid_resource");
        if (!resource.Current) return StrictDecision.Deny("object_not_current");
        if (!snapshot.Complete) return StrictDecision.Unknown("acl_snapshot_incomplete");
        if (resource.State != 0 || resource.AccessLevel != 0 || resource.StageId != 0 || resource.MandatoryAccess)
            return StrictDecision.Unknown("resource_security_features_unverified");
        if (resource.AccessObjectId <= 0) return StrictDecision.Unknown("access_area_unresolved");

        var area = resource.AccessObjectId;
        var potential = snapshot.Rows.Where(r => CouldApply(r, resource, principals)).ToArray();
        if (potential.Length == 0) return StrictDecision.Deny("no_explicit_read_rule", area);
        var allow = false;
        var deny = false;
        foreach (var row in potential)
        {
            // Unknown restrictions win over known positive assignments. Do not skip a default,
            // author/stage/link rule merely because an explicit allow was found first.
            if (row.UserId <= 0) return StrictDecision.Unknown("special_principal_unverified", area);
            if (row.AccessTypeId != 2 || row.ReferenceId != resource.ReferenceId || row.ObjectId <= 0)
                return StrictDecision.Unknown("assignment_scope_unverified", area);
            if (row.StageId != 0 || row.LinkTypeId != 0 || row.LinkId != 0 || row.Direction != 0 ||
                row.XAuthorId != 0 || row.XReferenceId != 0 || row.XObjectId != 0 ||
                row.XAccessObjectId != 0 || row.XReferenceAuthorGroupId != 0)
                return StrictDecision.Unknown("conditional_assignment_unverified", area);
            if (!snapshot.Groups.TryGetValue(row.AccessGroupId, out var group) || group.TypeId != 2)
                return StrictDecision.Unknown("access_group_unverified", area);
            if (!group.Commands.TryGetValue(verifiedReadCommandId.Value, out var read) || read is null)
                return StrictDecision.Unknown("read_command_unverified", area);
            if (read.Value) allow = true; else deny = true;
        }
        // Conservative deny-wins is a reporter restriction, not an invented native priority.
        if (deny) return StrictDecision.Deny("explicit_read_denied", area);
        return allow ? new(StrictOutcome.Allowed, "explicit_area_read", area)
            : StrictDecision.Deny("no_explicit_read_rule", area);
    }

    public static StrictDecision ReferenceReady(int reference, IReadOnlySet<long> principals,
        StrictAclSnapshot snapshot, int? verifiedReadCommandId)
    {
        if (!snapshot.Complete) return StrictDecision.Unknown("acl_snapshot_incomplete");
        if (principals.Count == 0) return StrictDecision.Deny("identity_unverified");
        if (verifiedReadCommandId is null or <= 0) return StrictDecision.Unknown("read_profile_unverified");
        var anyAllow = false;
        foreach (var row in snapshot.Rows)
        {
            if (row.ReferenceId != 0 && row.ReferenceId != reference && row.XReferenceId != reference) continue;
            var conditional = row.AccessTypeId != 2 || row.StageId != 0 || row.LinkTypeId != 0 || row.LinkId != 0 ||
                row.Direction != 0 || row.XAuthorId != 0 || row.XReferenceId != 0 || row.XObjectId != 0 ||
                row.XAccessObjectId != 0 || row.XReferenceAuthorGroupId != 0;
            if (!conditional && row.UserId > 0 && !principals.Contains(row.UserId)) continue;
            if (row.UserId <= 0) return StrictDecision.Unknown("special_principal_unverified");
            if (conditional || row.ReferenceId != reference || row.ObjectId <= 0)
                return StrictDecision.Unknown("reference_assignments_unverified");
            if (!snapshot.Groups.TryGetValue(row.AccessGroupId, out var group) || group.TypeId != 2 ||
                !group.Commands.TryGetValue(verifiedReadCommandId.Value, out var read) || read is null)
                return StrictDecision.Unknown("read_command_unverified");
            anyAllow |= read.Value;
        }
        return anyAllow ? new(StrictOutcome.Allowed, "reference_ready_for_object_checks") : StrictDecision.Deny("no_explicit_read_rule");
    }

    public static bool HasCycle(IEnumerable<(long Child, long Parent)> edges)
    {
        var graph = edges.Where(e => e.Parent > 0).GroupBy(e => e.Child)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Parent).Distinct().ToArray());
        var visiting = new HashSet<long>(); var visited = new HashSet<long>();
        bool Visit(long node)
        {
            if (visiting.Contains(node)) return true;
            if (!visited.Add(node)) return false;
            visiting.Add(node);
            if (graph.TryGetValue(node, out var parents))
                foreach (var parent in parents) if (Visit(parent)) return true;
            visiting.Remove(node); return false;
        }
        return graph.Keys.Any(Visit);
    }

    private static bool CouldApply(StrictAclRow row, StrictResource r, IReadOnlySet<long> principals)
    {
        if (row.ReferenceId != 0 && row.ReferenceId != r.ReferenceId && row.XReferenceId != r.ReferenceId) return false;
        // Nonzero x* conditions have unresolved semantics; retain them conservatively.
        var conditional = row.AccessTypeId != 2 || row.XAuthorId != 0 || row.XReferenceId != 0 || row.XObjectId != 0 ||
            row.XAccessObjectId != 0 || row.XReferenceAuthorGroupId != 0 || row.StageId != 0 ||
            row.LinkTypeId != 0 || row.LinkId != 0 || row.Direction != 0;
        if (!conditional && row.UserId > 0 && !principals.Contains(row.UserId)) return false;
        if (!conditional && row.ObjectId > 0 && row.ObjectId != r.ObjectId && row.ObjectId != r.AccessObjectId) return false;
        return true;
    }
}
