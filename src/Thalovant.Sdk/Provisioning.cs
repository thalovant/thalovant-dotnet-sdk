using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>
    /// Body for <c>POST /v1/hubs</c>. <see cref="Name"/> and <see cref="Spec"/> are the
    /// two fields the API requires; everything else is sent only when set, so the API
    /// applies its own defaults (<c>active</c> true, <c>visibility</c> "private",
    /// <c>capacity_profile</c> "standard").
    /// </summary>
    public sealed class CreateHubOptions
    {
        public string Name { get; }

        /// <summary>The hub spec, for example <c>{"protocols": {"wss": {"enabled": true}}}</c>.</summary>
        public JsonObject Spec { get; }

        public string? Slug { get; set; }

        /// <summary>Sent as <c>namespace</c>.</summary>
        public string? Namespace { get; set; }

        public string? RuntimeGroupId { get; set; }
        public string? Domain { get; set; }
        public bool? Active { get; set; }
        public string? Visibility { get; set; }

        /// <summary>Either <c>standard</c> or <c>autoscaling</c>.</summary>
        public string? CapacityProfile { get; set; }

        /// <summary>Admin-only; the API scopes a non-admin caller to their own tenant.</summary>
        public string? OwnerId { get; set; }

        /// <summary>
        /// Overrides the generated <c>Idempotency-Key</c> header, so a retry after a
        /// timeout can be made to return the hub the first attempt created.
        /// </summary>
        public string? IdempotencyKey { get; set; }

        public CreateHubOptions(string name, JsonObject spec)
        {
            Name = name;
            Spec = spec;
        }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject
            {
                ["name"] = Name,
                ["spec"] = JsonUtil.CloneObject(Spec),
            };
            if (Slug is not null)
            {
                body["slug"] = Slug;
            }
            if (Namespace is not null)
            {
                body["namespace"] = Namespace;
            }
            if (RuntimeGroupId is not null)
            {
                body["runtime_group_id"] = RuntimeGroupId;
            }
            if (Domain is not null)
            {
                body["domain"] = Domain;
            }
            if (Active is bool active)
            {
                body["active"] = active;
            }
            if (Visibility is not null)
            {
                body["visibility"] = Visibility;
            }
            if (CapacityProfile is not null)
            {
                body["capacity_profile"] = CapacityProfile;
            }
            if (OwnerId is not null)
            {
                body["owner_id"] = OwnerId;
            }
            return body;
        }
    }

    /// <summary>
    /// Body for <c>PATCH /v1/hubs/{hub_id}</c>. Every field is optional and sent only
    /// when set; the route itself always requires the hub's current
    /// <c>etag</c> as <c>If-Match</c>.
    /// </summary>
    public sealed class UpdateHubOptions
    {
        public string? Name { get; set; }
        public string? Slug { get; set; }

        /// <summary>Sent as <c>namespace</c>.</summary>
        public string? Namespace { get; set; }

        public string? RuntimeGroupId { get; set; }
        public string? Domain { get; set; }
        public bool? Active { get; set; }
        public string? Visibility { get; set; }

        /// <summary>Either <c>standard</c> or <c>autoscaling</c>.</summary>
        public string? CapacityProfile { get; set; }

        /// <summary>Admin-only; the API answers HTTP 403 for anyone else.</summary>
        public bool? IsLocked { get; set; }

        public JsonObject? Spec { get; set; }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject();
            if (Name is not null)
            {
                body["name"] = Name;
            }
            if (Slug is not null)
            {
                body["slug"] = Slug;
            }
            if (Namespace is not null)
            {
                body["namespace"] = Namespace;
            }
            if (RuntimeGroupId is not null)
            {
                body["runtime_group_id"] = RuntimeGroupId;
            }
            if (Domain is not null)
            {
                body["domain"] = Domain;
            }
            if (Active is bool active)
            {
                body["active"] = active;
            }
            if (Visibility is not null)
            {
                body["visibility"] = Visibility;
            }
            if (CapacityProfile is not null)
            {
                body["capacity_profile"] = CapacityProfile;
            }
            if (IsLocked is bool isLocked)
            {
                body["is_locked"] = isLocked;
            }
            if (Spec is not null)
            {
                body["spec"] = JsonUtil.CloneObject(Spec);
            }
            return body;
        }
    }

    /// <summary>
    /// Body for the release-apply routes
    /// (<c>POST /v1/hubs/{hub_id}/release</c> and
    /// <c>POST /v1/runtime-groups/{runtime_group_id}/release</c>). Every option is
    /// optional; omitted fields fall back to the workspace release policy, and passing
    /// <see cref="Images"/> switches to <c>custom</c> mode unless <see cref="Mode"/>
    /// says otherwise.
    /// </summary>
    public sealed class ReleaseOptions
    {
        public string? Channel { get; set; }
        public string? Mode { get; set; }
        public string? Version { get; set; }

        /// <summary>Component-to-image-reference overrides, sent as <c>images</c>.</summary>
        public IReadOnlyDictionary<string, string>? Images { get; set; }

        public string? Reason { get; set; }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject();
            if (Channel is not null)
            {
                body["channel"] = Channel;
            }
            if (Mode is not null)
            {
                body["mode"] = Mode;
            }
            if (Version is not null)
            {
                body["version"] = Version;
            }
            if (Images is not null)
            {
                var images = new JsonObject();
                foreach (var image in Images)
                {
                    images[image.Key] = image.Value;
                }
                body["images"] = images;
            }
            if (Reason is not null)
            {
                body["reason"] = Reason;
            }
            return body;
        }
    }

    /// <summary>Body for <c>POST /v1/runtime-groups</c>.</summary>
    public sealed class CreateRuntimeGroupOptions
    {
        public string Name { get; }
        public string? Description { get; set; }
        public string? Environment { get; set; }

        /// <summary>Admin-only; the API scopes a non-admin caller to their own tenant.</summary>
        public string? OwnerId { get; set; }

        /// <summary>Seeds the new group from the workspace default group.</summary>
        public bool? CloneFromDefault { get; set; }

        public CreateRuntimeGroupOptions(string name)
        {
            Name = name;
        }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject { ["name"] = Name };
            if (Description is not null)
            {
                body["description"] = Description;
            }
            if (Environment is not null)
            {
                body["environment"] = Environment;
            }
            if (OwnerId is not null)
            {
                body["owner_id"] = OwnerId;
            }
            if (CloneFromDefault is bool cloneFromDefault)
            {
                body["clone_from_default"] = cloneFromDefault;
            }
            return body;
        }
    }

    /// <summary>
    /// Body for <c>PATCH /v1/runtime-groups/{runtime_group_id}</c>. Unlike the hub
    /// update route this one takes no <c>If-Match</c>.
    /// </summary>
    public sealed class UpdateRuntimeGroupOptions
    {
        public string? Name { get; set; }
        public string? Description { get; set; }

        /// <summary>Patches <c>replicas</c> and the container <c>resources</c>.</summary>
        public JsonObject? Spec { get; set; }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject();
            if (Name is not null)
            {
                body["name"] = Name;
            }
            if (Description is not null)
            {
                body["description"] = Description;
            }
            if (Spec is not null)
            {
                body["spec"] = JsonUtil.CloneObject(Spec);
            }
            return body;
        }
    }

    /// <summary>Filters for <c>GET /v1/marketplace/skills</c>.</summary>
    public sealed class MarketplaceSkillListOptions
    {
        /// <summary>
        /// Admin-only. The API silently scopes a non-admin caller to their own tenant
        /// instead of rejecting the parameter.
        /// </summary>
        public string? OwnerId { get; set; }

        /// <summary>Admin-only, and likewise ignored for non-admin callers.</summary>
        public bool IncludeInactive { get; set; }

        /// <summary>Re-syncs the global catalog from its source first, which is slower.</summary>
        public bool ForceRefresh { get; set; }
    }

    /// <summary>
    /// Body for <c>POST /v1/runtime-groups/{runtime_group_id}/skills</c>. The default
    /// <see cref="SourceType"/> of <c>catalog</c> installs a marketplace skill and
    /// requires the skill to exist in the catalog; <c>git</c> installs need a
    /// <see cref="SourceRef"/> repository URL.
    /// </summary>
    public sealed class InstallRuntimeGroupSkillOptions
    {
        public string SkillId { get; }

        /// <summary>The catalog entry's id, when installing a specific marketplace row.</summary>
        public string? MarketplaceSkillId { get; set; }

        public string SourceType { get; set; } = "catalog";
        public string? SourceRef { get; set; }
        public string? VersionPin { get; set; }
        public bool Active { get; set; } = true;

        public InstallRuntimeGroupSkillOptions(string skillId)
        {
            SkillId = skillId;
        }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject
            {
                ["skill_id"] = SkillId,
                ["source_type"] = SourceType,
                ["active"] = Active,
            };
            if (MarketplaceSkillId is not null)
            {
                body["marketplace_skill_id"] = MarketplaceSkillId;
            }
            if (SourceRef is not null)
            {
                body["source_ref"] = SourceRef;
            }
            if (VersionPin is not null)
            {
                body["version_pin"] = VersionPin;
            }
            return body;
        }
    }
}
