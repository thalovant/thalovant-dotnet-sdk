using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Thalovant {
    // OVOS-compatible langcodes 3.5.1 CLDR distances; MIT attribution is in LICENSE-langcodes.
    internal static class LanguageMatching {
        private static readonly JsonObject Data = ListingRules.LoadResource("language-matching.json");
        private static string? Field(string section,string key) => (Data[section] as JsonObject)?[key]?.GetValue<string>();
        private sealed class Tag { internal string Language="und",Script="",Region=""; }
        private static bool Script(string value) => value.Length==4 && value.All(c=>c>='a'&&c<='z');
        private static bool Region(string value) => (value.Length==2&&value.All(c=>c>='a'&&c<='z'))||(value.Length==3&&value.All(c=>c>='0'&&c<='9'));
        private static Tag Parse(string raw,bool aliases=true) {
            var value=raw.Trim().Replace('_','-').ToLowerInvariant();
            if (aliases) value=Field("languages",value)?.ToLowerInvariant() ?? value;
            var tokens=value.Split('-');var primary=tokens[0].Length==0?"und":tokens[0];
            var alias=aliases?Field("languages",primary):null;var tag=alias==null?new Tag{Language=primary}:Parse(alias,false);
            bool onlyScript=true;
            foreach(var token in tokens.Skip(1)) {
                if(!Script(token)) onlyScript=false;
                if(token.Length==1) break;
                if(Script(token)) tag.Script=Field("scripts",token) ?? token.Substring(0,1).ToUpperInvariant()+token.Substring(1);
                else if(Region(token)) tag.Region=Field("territories",token) ?? token.ToUpperInvariant();
            }
            if(tag.Script==Field("default_scripts",tag.Language)) tag.Script="";
            if(tag.Language=="pt"&&tag.Script.Length==0&&tag.Region.Length==0&&onlyScript) tag.Region="PT";
            return tag;
        }
        private static Tag Maximize(Tag tag) {
            if(tag.Language=="und"&&tag.Script.Length==0&&tag.Region.Length==0) return new Tag{Language="und",Script="Zzzz",Region="ZZ"};
            tag.Language=Field("macrolanguages",tag.Language) ?? tag.Language;
            string Join(params string[] parts)=>string.Join("-",parts.Where(p=>p.Length>0));
            var probes=new List<string>{Join(tag.Language,tag.Script,tag.Region),Join(tag.Language,tag.Region),Join(tag.Language,tag.Script),tag.Language};
            if(tag.Script.Length>0) probes.Add("und-"+tag.Script);probes.Add("und");
            var likely=probes.Select(p=>Field("likely",p)).First(v=>v!=null)!.Split('-');
            if(tag.Language=="und") tag.Language=likely[0];if(tag.Script.Length==0) tag.Script=likely[1];if(tag.Region.Length==0) tag.Region=likely[2];return tag;
        }
        private static int Distance(string target,string candidate) {
            var a=Maximize(Parse(target));var b=Maximize(Parse(candidate));
            int Lookup(string from,string to,int fallback)=>(Data["distances"]?[from] as JsonObject)?[to]?.GetValue<int>() ?? fallback;
            int result=a.Language==b.Language?0:Lookup(a.Language,b.Language,80);
            var pa=a.Language+"_"+a.Script;var pb=b.Language+"_"+b.Script;
            if(a.Script!=b.Script) result+=Lookup(pa,pb,50);if(a.Region==b.Region)return result;
            bool Inside(string group,string region)=>((JsonArray)Data["regions"]![group]!).Any(v=>v!.GetValue<string>()==region);
            int td=4;
            if(pa==pb) {
                if(a.Language=="ar") {if(Inside("MAGHREB",a.Region)!=Inside("MAGHREB",b.Region))td=5;}
                else if(a.Language=="en") {
                    if((a.Region=="GB"&&!Inside("US",b.Region))||(!Inside("US",a.Region)&&b.Region=="GB"))td=3;
                    else if(Inside("US",a.Region)!=Inside("US",b.Region))td=5;
                } else if(Inside("LATIN_AMERICA",a.Region)&&b.Region=="419")td=1;
                else if(a.Language=="es"||a.Language=="pt") {if(Inside("AMERICAS",a.Region)!=Inside("AMERICAS",b.Region))td=5;}
                else if(pa=="zh_Hant"&&Inside("CNSAR",a.Region)!=Inside("CNSAR",b.Region))td=5;
            }
            return result+td;
        }
        /// <summary>
        /// The form a language is usually written in, when that differs from
        /// <paramref name="tag"/>: "en-CA" and "en-AT" both to "en-us",
        /// "fr-BE" to "fr-fr", "pt-AO" to "pt-br", from CLDR's likely
        /// subtags. Null when there is nothing different to try, so a caller
        /// can tell "already the usual form" from "no idea".
        /// </summary>
        /// <remarks>
        /// Listing and asking do not agree about languages. A hub matches an
        /// utterance to the closest language it knows, so a phone set to
        /// "en-CA" is understood by skills registered under "en-US"; its
        /// manifest is keyed by exact tag, so the same hub lists nothing for
        /// "en-CA". Lower case, because that is how skills register and how
        /// the manifest is keyed: an exact lookup with BCP47's "en-US" finds
        /// nothing.
        /// </remarks>
        internal static string? Usual(string tag) {
            if(string.IsNullOrWhiteSpace(tag)) return null;
            var baseLanguage=Parse(tag).Language;
            // "und" is the tag for "no idea", and Parse produces it for
            // anything it cannot read. CLDR's guess for an unknown language
            // is English, so without this a blank tag lists a hub in a
            // language nobody asked for.
            if(baseLanguage.Length==0||baseLanguage=="und") return null;
            // Maximize does not fail on a language it has never heard of: it
            // walks its probes down to "und" and takes the root locale's
            // region, so "zzz" comes back "zzz-us". Round-tripping the tag
            // does not catch that, because the unknown language is carried
            // through unchanged. A direct entry in the likely table is what
            // says CLDR has heard of this language.
            if(Field("likely",baseLanguage)==null) return null;
            var likely=Maximize(new Tag{Language=baseLanguage});
            var usual=(likely.Region.Length==0?likely.Language:likely.Language+"-"+likely.Region).ToLowerInvariant();
            // Byte comparison, NOT sameLanguage. They are not the same test, and the difference is the whole point: the canonical spelling is en-US, the manifest is keyed en-us, and sameLanguage calls those equal -- so the retry that exists for exactly this case suppressed itself.
            return usual==tag.Trim()?null:usual;
        }

        private static bool SameLanguageTag(string a,string b)=>
            a.Trim().Replace('_','-').ToLowerInvariant()==b.Trim().Replace('_','-').ToLowerInvariant();

        internal static string? Closest(string target,IEnumerable<string> available) {
            string? best=null;int minimum=int.MaxValue;
            foreach(var candidate in available) {int distance=Distance(target,candidate);if(distance<minimum){minimum=distance;best=candidate;}}
            return minimum<=10?best:null;
        }
    }
    public static partial class ThalovantContext {
        public static string? ClosestLanguage(string target,IEnumerable<string> available)=>LanguageMatching.Closest(target,available);

        /// <summary>The tag to retry a listing with; see <c>LanguageMatching.Usual</c>.</summary>
        public static string? UsualForm(string tag)=>LanguageMatching.Usual(tag);
    }
}
