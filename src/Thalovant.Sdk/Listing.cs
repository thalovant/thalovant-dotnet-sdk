using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Thalovant {
    /// <summary>Immutable complete language-rule snapshot, safe for concurrent readers.</summary>
    public sealed class ListingRules {
        private static readonly Lazy<ListingRules> Bundled = new Lazy<ListingRules>(()=>new ListingRules(LoadResource("listing.json")));
        private static readonly Lazy<JsonObject> Upper = new Lazy<JsonObject>(()=>(JsonObject)LoadResource("unicode-upper.json")["expansions"]!);
        public static ListingRules Default => Bundled.Value;
        private readonly JsonObject _languages;
        private readonly Dictionary<string,Regex[]> _patterns = new Dictionary<string,Regex[]>(StringComparer.Ordinal);
        private readonly Dictionary<string,List<KeyValuePair<Regex,string>>> _written = new Dictionary<string,List<KeyValuePair<Regex,string>>>(StringComparer.Ordinal);
        public bool Available {get;}
        public string SentenceEnds {get;}
        internal static JsonObject LoadResource(string name) {
            using var stream=typeof(ListingRules).Assembly.GetManifestResourceStream("Thalovant.ListingData."+name) ?? throw new InvalidOperationException("Missing bundled listing data: "+name);
            using var reader=new StreamReader(stream,Encoding.UTF8);return JsonUtil.ParseObject(reader.ReadToEnd());
        }
        public ListingRules():this(LoadResource("listing.json")) {}
        /// <summary>Null selects bare rendering. Invalid regex patterns fail construction.</summary>
        public ListingRules(JsonObject? data) {
            Available=data!=null;var snapshot=JsonUtil.CloneObject(data);
            SentenceEnds=snapshot["sentence_ends"]?.GetValue<string>() ?? "";
            _languages=snapshot["languages"] as JsonObject ?? new JsonObject();
            foreach(var pair in _languages) {
                var language=(JsonObject)pair.Value!;
                _patterns[pair.Key]=Values(language,"question_patterns").Select(p=>Compile(p,true)).ToArray();
                _written[pair.Key]=new List<KeyValuePair<Regex,string>>();
                foreach(var word in language["written_forms"] as JsonObject ?? new JsonObject())
                    _written[pair.Key].Add(new KeyValuePair<Regex,string>(Compile(@"\b"+Regex.Escape(word.Key)+@"\b",false),word.Value!.GetValue<string>()));
            }
        }
        private static Regex Compile(string expression,bool ignoreCase) {
            const string boundary=@"(?:(?<![\p{L}\p{N}_])(?=[\p{L}\p{N}_])|(?<=[\p{L}\p{N}_])(?![\p{L}\p{N}_]))";
            var output=new StringBuilder();bool inClass=false;
            for(int i=0;i<expression.Length;i++) {
                char c=expression[i];
                if(c=='\\'&&i+1<expression.Length){char next=expression[++i];if(next=='b'&&!inClass)output.Append(boundary);else output.Append(c).Append(next);}
                else {if(c=='[')inClass=true;if(c==']')inClass=false;output.Append(c);}
            }
            return new Regex(output.ToString(),RegexOptions.CultureInvariant|(ignoreCase?RegexOptions.IgnoreCase:RegexOptions.None),TimeSpan.FromMilliseconds(100));
        }
        private static IEnumerable<string> Values(JsonObject data,string key)=>(data[key] as JsonArray)?.Select(v=>v!.GetValue<string>()) ?? Enumerable.Empty<string>();
        private string? Tag(string? lang)=>string.IsNullOrEmpty(lang)||_languages.Count==0?null:LanguageMatching.Closest(lang!,_languages.Select(p=>p.Key));
        public JsonObject LanguageData(string? lang)=>Tag(lang) is string tag?JsonUtil.CloneObject((JsonObject)_languages[tag]!):new JsonObject();
        private HashSet<string> Words(string? lang,string key) {
            var values=string.IsNullOrEmpty(lang)?_languages.Select(p=>(JsonObject)p.Value!):new[]{LanguageData(lang)};
            return new HashSet<string>(values.SelectMany(v=>Values(v,key)).Select(v=>v.ToLowerInvariant()),StringComparer.Ordinal);
        }
        private static string[] Tokens(string text)=>text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        public bool Dangling(string text,string? lang=null) {
            var words=Tokens(text.TrimEnd((SentenceEnds+" ").ToCharArray()));return words.Length>0&&Words(lang,"trailing_words").Contains(words[words.Length-1].ToLowerInvariant());
        }
        /// <summary>Recognize questions; RegexMatchTimeoutException reports a rule exceeding 100ms.</summary>
        public bool Asks(string text,string? lang=null) {
            var tag=Tag(lang);if(tag!=null&&_patterns[tag].Any(p=>p.IsMatch(text)))return true;
            var words=Tokens(text).Select(w=>w.Trim(",;:!?.’'\"()".ToCharArray()).ToLowerInvariant()).Where(w=>w.Length>0).ToArray();
            var openers=Words(lang,"question_openers");var anywhere=Words(lang,"question_words_anywhere");
            return words.Length>0&&(openers.Contains(words[0])||words.Any(anywhere.Contains));
        }
        /// <summary>Known rules add punctuation; unknown or timed-out rules leave a bare line.</summary>
        public string AsSentence(string text,string? lang=null) {
            text=text.Trim();if(text.Length==0)return text;
            int first=char.IsHighSurrogate(text[0])&&text.Length>1&&char.IsLowSurrogate(text[1])?2:1;
            string scalar=text.Substring(0,first);text=(Upper.Value[scalar]?.GetValue<string>() ?? scalar.ToUpperInvariant())+text.Substring(first);
            if(SentenceEnds.IndexOf(text[text.Length-1])>=0||Dangling(text,lang))return text;
            var tag=Tag(lang);if(tag==null)return text;var data=(JsonObject)_languages[tag]!;
            if(!new[]{"question_openers","question_words_anywhere","question_patterns"}.Any(key=>Values(data,key).Any()))return text;
            try {
                foreach(var rule in _written[tag])text=rule.Key.Replace(text,_=>rule.Value);
                return text+(Asks(text,lang)?"?":".");
            } catch(RegexMatchTimeoutException) {return text;}
        }
        public string Speakable(string pattern,IReadOnlyDictionary<string,string>? slots=null,string? lang=null) {
            var merged=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in LanguageData(lang)["slot_examples"] as JsonObject ?? new JsonObject())merged[pair.Key]=pair.Value!.GetValue<string>();
            if(slots!=null)foreach(var pair in slots)merged[pair.Key]=pair.Value;
            return ThalovantContext.Speakable(pattern,merged);
        }
        private static int ScalarLength(string text) {
            int count=0;for(int i=0;i<text.Length;i++,count++)if(char.IsHighSurrogate(text[i])&&i+1<text.Length&&char.IsLowSurrogate(text[i+1]))i++;return count;
        }
        public IReadOnlyList<string> Rank(IEnumerable<string> phrases,string? lang=null)=>phrases.OrderBy(s=>Dangling(s,lang)).ThenBy(s=>s.IndexOf('{')>=0).ThenByDescending(s=>Math.Min(Tokens(s).Length,8)).ThenBy(ScalarLength).ToArray();
    }
    public static partial class ThalovantContext {
        public static string AsSentence(string text,string? lang=null,ListingRules? listing=null)=>(listing??ListingRules.Default).AsSentence(text,lang);
        public static string SpeakableWithLanguage(string pattern,IReadOnlyDictionary<string,string>? slots=null,string? lang=null,ListingRules? listing=null)=>(listing??ListingRules.Default).Speakable(pattern,slots,lang);
    }
}
