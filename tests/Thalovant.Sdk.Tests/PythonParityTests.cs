using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests {
    public class PythonParityTests {
        private static JsonObject Obj(string value) => JsonNode.Parse(value)!.AsObject();
        [Fact] public void HintsAndLocationPreserveCaller() {
            var original=Obj("""{"session":{"pipeline":["old"],"session_id":"kept"}}""");
            var location=ThalovantContext.BuildLocation(" Montréal ",country:" ca ",latitude:45.5,longitude:-73.5)!;
            var result=ThalovantContext.RequestContext(original," fr ",new[]{" ","intent"},location)!;
            Assert.Equal("fr",(string?)result["stt_lang"]);Assert.Equal("CA",(string?)location["country_code"]);
            Assert.Equal("old",(string?)original["session"]!["pipeline"]![0]);Assert.Null(ThalovantContext.RequestContext());Assert.Null(ThalovantContext.BuildLocation());
            foreach(var (lat,lon) in new[]{(0.0,0.0),(91.0,0.0),(0.0,181.0),(double.NaN,1.0)}) Assert.Null(ThalovantContext.BuildLocation("Toronto",latitude:lat,longitude:lon)!["coordinate"]);
        }
        [Fact] public void SpeakableRetainsSourcePriorityAndBestDuplicateRank() {
            Assert.Equal("did i ask about thing",ThalovantContext.Speakable("did i (already |)ask (about|for|to|) {thing}"));
            var intent=new HubIntent("x","x","padatious",new Dictionary<string,IReadOnlyList<string>>{{"en-us",new[]{"{x}","a complete sentence","[please]","(x|y)","x"}}});
            Assert.Equal(new[]{"x","a complete sentence"},intent.ExamplesWithOptions("en-us",2,true));
        }
        [Fact] public void AudioIsBoundedStrictAndCollectedInOrder() {
            var e=new ThalovantEvent(ThalovantEvents.AudioQueue,Obj("""{"binary_data":"00 ff\n10","lang":"fr"}"""),Obj("""{"request_id":"r"}"""));
            Assert.Equal(new byte[]{0,255,16},e.AudioBytes());Assert.Equal("fr",e.Lang);
            foreach(var value in new[]{"","0","0 0","gg","https://example.com","00\u00a0ff"}) Assert.ThrowsAny<Exception>(()=>new ThalovantEvent(ThalovantEvents.AudioQueue,new JsonObject{["binary_data"]=value}).AudioBytes());
            var state=new AskState();state.Process(e,"r");state.Process(e,"r");
            var clip=new string('0',ThalovantEvents.MaxAudioClipBytes*2);
            for(int i=0;i<4;i++) state.Process(new ThalovantEvent(ThalovantEvents.AudioQueue,new JsonObject{["binary_data"]=clip},Obj("""{"request_id":"r"}""")),"r");
            state.Process(new ThalovantEvent(ThalovantEvents.AudioQueue,new JsonObject{["binary_data"]=clip+"00"},Obj("""{"request_id":"r"}""")),"r");
            Assert.Equal(2,state.Snapshot().DroppedMedia);Assert.Equal(4,state.Snapshot().Events.Count);Assert.False(state.ReplyGate.IsOpen);
        }
        [Fact] public async Task ConfigConflictsRereadAndPreserveConcurrentKeys() {
            var handler=new StubHttpMessageHandler();var api=new ThalovantControlPlane(apiUrl:"https://api.example.com",accessToken:"test",httpMessageHandler:handler);
            handler.Enqueue(200,"{\"config\":{\"nested\":{\"original\":true}},\"revision\":\""+new string('a',64)+"\"}");handler.Enqueue(412,"{}");
            handler.Enqueue(200,"{\"config\":{\"nested\":{\"original\":true,\"concurrent\":true}},\"revision\":\""+new string('b',64)+"\"}");handler.Enqueue(200,"{}");
            var delta=Obj("""{"nested":{"caller":true},"array":[1]}""");
            await api.UpdateRuntimeGroupConfigAsync("x/y",delta,new JsonObject());
            Assert.Equal(new[]{"GET","PUT","GET","PUT"},handler.Requests.Select(r=>r.Method));
            var body=handler.Requests[3].BodyObject()!;
            Assert.True(JsonNode.DeepEquals(Obj("""{"nested":{"original":true,"concurrent":true,"caller":true},"array":[1]}"""),body["config"]));
            Assert.Equal(new string('b',64),(string?)body["expected_revision"]);Assert.Empty(body["personas"]!.AsObject());Assert.Single(delta["nested"]!.AsObject());
        }
        [Theory][InlineData(400)][InlineData(401)][InlineData(403)][InlineData(405)][InlineData(409)][InlineData(412)][InlineData(429)][InlineData(500)]
        public async Task ConfigOnlyRetriesPreconditions(int status) {
            var handler=new StubHttpMessageHandler();var api=new ThalovantControlPlane(apiUrl:"https://api.example.com",accessToken:"test",httpMessageHandler:handler);
            int attempts=status==412?3:1;
            for(int i=0;i<attempts;i++){handler.Enqueue(200,"{\"config\":{},\"revision\":\""+new string('a',64)+"\"}");handler.Enqueue(status,"{}");}
            var error=await Assert.ThrowsAsync<ThalovantApiException>(()=>api.UpdateRuntimeGroupConfigAsync("x",new JsonObject()));Assert.Equal(status,error.StatusCode);Assert.Equal(attempts*2,handler.Requests.Count);
        }
        [Theory][InlineData("{\"config\":{}}")][InlineData("{\"config\":{},\"revision\":\"bad\"}")]
        public async Task OlderServerFailsBeforeAnyWrite(string body) {
            var handler=new StubHttpMessageHandler();var api=new ThalovantControlPlane(apiUrl:"https://api.example.com",accessToken:"test",httpMessageHandler:handler);
            handler.Enqueue(200,body);await Assert.ThrowsAsync<ThalovantApiException>(()=>api.UpdateRuntimeGroupConfigAsync("x",new JsonObject()));Assert.Single(handler.Requests);Assert.Equal("GET",handler.Requests[0].Method);
        }
    }
}
