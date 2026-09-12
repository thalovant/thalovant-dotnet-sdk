using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Thalovant.Sdk.Tests {
    public class ListingTests {
        private static JsonObject Fixture(string name)=>(JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",name)))!;
        [Fact] public void PublishedPythonListingReferenceCases() {
            foreach(var node in (JsonArray)Fixture("listing-vectors.json")["cases"]!) {
                var row=(JsonObject)node!;var lang=row["lang"]?.GetValue<string>();var kind=row["kind"]!.GetValue<string>();
                if(kind=="rank") {
                    var phrases=((JsonArray)row["phrases"]!).Select(v=>v!.GetValue<string>());
                    var expected=((JsonArray)row["expected"]!).Select(v=>v!.GetValue<string>());
                    Assert.Equal(expected,ListingRules.Default.Rank(phrases,lang));
                } else {
                    var text=row["text"]!.GetValue<string>();
                    var actual=kind=="sentence"?ThalovantContext.AsSentence(text,lang):ThalovantContext.SpeakableWithLanguage(text,lang:lang);
                    Assert.Equal(row["expected"]!.GetValue<string>(),actual);
                }
            }
        }
        [Fact] public void OvosLanguageMatchingReferenceCases() {
            foreach(var node in (JsonArray)Fixture("language-matching-vectors.json")["cases"]!) {
                var row=(JsonObject)node!;var available=((JsonArray)row["available"]!).Select(v=>v!.GetValue<string>());
                Assert.Equal(row["expected"]?.GetValue<string>(),ThalovantContext.ClosestLanguage(row["target"]!.GetValue<string>(),available));
            }
        }
        [Fact] public void SelectedLocaleAndRenderedLimits() {
            var intent=new HubIntent("s","n","padatious",new Dictionary<string,IReadOnlyList<string>>{{"fr-FR",new[]{"volume {level} pour cent"}},{"en-US",new[]{"volume {level} percent"}}});
            Assert.Equal(new[]{"Volume cinquante pour cent."},intent.ExamplesWithListing(sentence:true));
            Assert.Equal(new[]{"Volume ten percent."},intent.ExamplesWithListing("en-GB",sentence:true,slots:new Dictionary<string,string>{{"level","ten"}}));
            intent=new HubIntent("s","n","padatious",new Dictionary<string,IReadOnlyList<string>>{{"en-US",new[]{"[please]","(repeat|say) that (again|)","[please] repeat that","volume [to] {level} percent"}}});
            foreach(int limit in new[]{0,2})Assert.Equal(new[]{"Repeat that.","Volume fifty percent."},intent.ExamplesWithListing("en-US",limit,sentence:true));
            Assert.Equal(intent.Phrases["en-US"],intent.Examples("en-US",0));
        }
        [Fact] public void IndependentOptionalRulesAndSnapshot() {
            var data=(JsonObject)JsonNode.Parse("""{"sentence_ends":".!?","languages":{"xq":{"question_patterns":["(?i)^is it","(?m)^can it"],"slot_examples":{"thing":"the widget"}}}}""")!;
            var listing=new ListingRules(data);data["languages"]!["xq"]!["question_patterns"]![0]="never";
            Assert.Equal("Is it ready?",listing.AsSentence("is it ready","xq"));
            Assert.Equal("Can it work?",listing.AsSentence("can it work","xq"));
            Assert.Equal("Go home.",listing.AsSentence("go home","xq"));
            Assert.Equal("What time is it",listing.AsSentence("what time is it","en"));
            Assert.Equal("open the widget",listing.Speakable("open {thing}",lang:"xq-ZZ"));
            listing.LanguageData("xq")["question_patterns"]![0]="broken";
            Assert.True(listing.Asks("IS IT ready","xq"));
            var anywhere=new ListingRules((JsonObject)JsonNode.Parse("""{"sentence_ends":".!?","languages":{"xq":{"question_words_anywhere":["plim"]}}}""")!);
            Assert.Equal("Go plim now?",anywhere.AsSentence("go plim now","xq"));
        }
        [Fact] public void BareDataAndInvalidOrExpensiveRules() {
            var bare=new ListingRules(null);Assert.False(bare.Available);
            Assert.Equal("Do i need a jacket",bare.AsSentence("do i need a jacket","en"));
            Assert.Equal("volume level percent",bare.Speakable("volume [to] {level} percent",lang:"en"));
            Assert.ThrowsAny<ArgumentException>(()=>new ListingRules((JsonObject)JsonNode.Parse("""{"languages":{"xq":{"question_patterns":["("]}}}""")!));
            var costly=new ListingRules((JsonObject)JsonNode.Parse("""{"sentence_ends":".!?","languages":{"xq":{"question_patterns":["(a+)+$"]}}}""")!);
            var text=new string('a',100000)+"x";
            Assert.Throws<RegexMatchTimeoutException>(()=>costly.Asks(text,"xq"));
            Assert.True(costly.AsSentence(text,"xq")=="A"+text.Substring(1));
        }

        [Fact] public void SentenceMarksCompareWholeUnicodeScalars() {
            var rules=ListingRules.Default;
            Assert.Equal("Go \U00010441.",rules.AsSentence("go \U00010441","en"));
            Assert.Equal("Go \U00011C41",rules.AsSentence("go \U00011C41","en"));
            Assert.False(rules.Dangling("weather in\U00010441","en"));
            Assert.True(rules.Dangling("weather in\U00011C41","en"));
        }

        [Fact] public void UnicodeNonBoundaries() {
            var rules=new ListingRules((JsonObject)JsonNode.Parse("""{"languages":{"xq":{"question_patterns":["\\Bété\\B"]}}}""")!);
            Assert.False(rules.Asks("été","xq"));
            Assert.True(rules.Asks("pétéx","xq"));
        }
    }
}
