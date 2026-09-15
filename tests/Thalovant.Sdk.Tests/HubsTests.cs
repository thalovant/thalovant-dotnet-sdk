using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>What a phone calls a hub. It called them slugs until 2026-09-15.</summary>
    public class HubsTests
    {
        private static JsonObject Hub(string json) => JsonNode.Parse(json)!.AsObject();

        [Fact]
        public void AHubIsCalledWhatAPersonWasShown()
        {
            // Exactly what a phone was offered: name IS the slug, and the
            // readable title sits in the catalog entry.
            Assert.Equal("Ops Copilot", Hubs.DisplayName(Hub(
                """{"name":"ops-copilot","slug":"ops-copilot","spec":{"catalog":{"title":"Ops Copilot"}}}""")));
        }

        [Fact]
        public void ARealNameWinsWhenThereIsNoCatalogEntry()
        {
            Assert.Equal("The Kitchen", Hubs.DisplayName(Hub("""{"name":"The Kitchen","slug":"kitchen"}""")));
        }

        [Fact]
        public void ASlugIsMadeReadableRatherThanShownRaw()
        {
            Assert.Equal("Daily Desk", Hubs.DisplayName(Hub("""{"slug":"daily-desk"}""")));
            Assert.Equal("Local Pulse", Hubs.DisplayName(Hub("""{"slug":"local_pulse"}""")));
            Assert.Equal("News Stream", Hubs.DisplayName(Hub("""{"name":"news-stream","slug":"news-stream"}""")));
        }

        [Theory]
        [InlineData("""{"id":"1"}""")]
        [InlineData("""{"name":"","slug":"   "}""")]
        [InlineData("""{"spec":"nonsense"}""")]
        [InlineData("""{"spec":{"catalog":[]}}""")]
        public void AHubDescribedWithNothingStillSaysSomething(string json)
        {
            Assert.Equal("A Thalovant hub", Hubs.DisplayName(Hub(json)));
        }
    }
}
