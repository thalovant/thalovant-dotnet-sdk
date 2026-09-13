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
            var reply = new ThalovantReply("reply", "reply", Array.Empty<string>(), handled, handled && !failed, null, null,
                row["contexts"]!.AsArray().Select(context => new ThalovantEvent("speak", context: context!.AsObject())).ToArray(), failed ? new ThalovantEvent("failure") : null);
            Assert.Equal(row["expected"]!["pipeline_ids"]!.AsArray().Select(x => x!.GetValue<string>()), reply.PipelineIds);
            Assert.Equal(row["expected"]!["skill_ids"]!.AsArray().Select(x => x!.GetValue<string>()), reply.SkillIds);
            Assert.Equal(row["expected"]!["claimed"]!.GetValue<bool>(), reply.Claimed);
        }
    }
}
