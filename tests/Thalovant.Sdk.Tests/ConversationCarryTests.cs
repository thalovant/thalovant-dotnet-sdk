using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
namespace Thalovant.Sdk.Tests;

/// <summary>
/// Carrying a conversation between the turns of a named session.
/// </summary>
/// <remarks>
/// A hub keeps nothing for one: OVOS-SESSION-2 §2.2 makes the orchestrator
/// stateless, so the carrier a client sends is the whole snapshot and whatever
/// the last turn activated is discarded the moment it ends. Without
/// converse_handlers the converse pipeline has no skill to poll and every
/// follow-up reaches the fallback instead of the skill that just answered.
/// <para>
/// The cases are contracts/conformance/conversation-vectors.json and
/// mesh-vectors.json, shared with every other SDK so that being on par is
/// something a machine checks rather than something a digest asserts.
/// </para>
/// </remarks>
public sealed class ConversationCarryTests {
    private static JsonNode Vectors(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;

    [Fact] public void TheCarryMatchesTheSharedVectors() {
        foreach (var item in Vectors("conversation-vectors.json")["cases"]!.AsArray()) {
            var row = item!;
            var previous = row["previous"]!.AsObject();
            var session = row["session"]!.AsObject();
            var carried = ThalovantContext.CarryConversation(previous, session);
            // Recorded before the assert: what this SDK produced, not a
            // restatement of what the vector says it should have.
            ConformanceRecord.Record("conversation-vectors.json", (string)row["name"]!, carried.DeepClone());
            Assert.Equal(row["expected"]!.ToJsonString(), carried.ToJsonString());
        }
    }

    [Fact] public void TheCarriedFieldsAreTheOnesTheVectorsName() {
        var want = Vectors("conversation-vectors.json")["carried_fields"]!.AsArray()
            .Select(field => field!.GetValue<string>()).OrderBy(field => field);
        Assert.Equal(want, ThalovantContext.ConversationSessionFields.OrderBy(field => field));
    }

    [Fact] public void TheFieldsTheVectorsForbidNeverTravel() {
        // A remembered lang would pin a bilingual conversation to whichever
        // language it opened in, which is the failure this list prevents.
        foreach (var item in Vectors("conversation-vectors.json")["never_carried"]!.AsArray()) {
            Assert.DoesNotContain(item!.GetValue<string>(), ThalovantContext.ConversationSessionFields);
        }
    }

    [Fact] public void TheHiveKindsAreTheOnesTheVectorsName() {
        var want = Vectors("mesh-vectors.json")["kinds"]!.AsArray()
            .Select(kind => kind!.GetValue<string>()).OrderBy(kind => kind);
        Assert.Equal(want, ThalovantContext.HiveKinds.OrderBy(kind => kind));
    }

    [Fact] public void ThisClientsOwnTrafficIsNotAHiveKind() {
        // query and cascade belong to AskAsync; subscribing to one here would
        // quietly compete for the same replies.
        foreach (var item in Vectors("mesh-vectors.json")["refused_kinds"]!.AsArray()) {
            Assert.DoesNotContain(item!.GetValue<string>(), ThalovantContext.HiveKinds);
        }
    }
}
