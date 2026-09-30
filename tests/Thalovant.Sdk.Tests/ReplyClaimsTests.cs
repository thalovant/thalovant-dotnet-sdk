using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
namespace Thalovant.Sdk.Tests;
public sealed class ReplyClaimsTests {
    [Fact] public void SharedReplyClaimVectors() {
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reply-claim-vectors.json")))!;
        foreach (var item in data["cases"]!.AsArray()) {
            var row = item!; var handled = row["handled"]!.GetValue<bool>(); var failed = row["failed"]!.GetValue<bool>();
            var contexts = row["contexts"]!.AsArray();
            var metas = row["metas"] as JsonArray;
            var events = contexts.Select((context, index) => {
                JsonObject? eventData = null;
                var meta = metas is not null && index < metas.Count ? metas[index] : null;
                if (meta is JsonObject metaObject) {
                    eventData = new JsonObject { ["meta"] = metaObject.DeepClone() };
                }
                return new ThalovantEvent("speak", data: eventData, context: context!.AsObject());
            }).ToArray();
            var reply = new ThalovantReply("reply", "reply", Array.Empty<string>(), handled, handled && !failed, null, null,
                events, failed ? new ThalovantEvent("failure") : null);
            Assert.Equal(row["expected"]!["pipeline_ids"]!.AsArray().Select(x => x!.GetValue<string>()), reply.PipelineIds);
            Assert.Equal(row["expected"]!["skill_ids"]!.AsArray().Select(x => x!.GetValue<string>()), reply.SkillIds);
            Assert.Equal(row["expected"]!["claimed"]!.GetValue<bool>(), reply.Claimed);
        }
    }
}
