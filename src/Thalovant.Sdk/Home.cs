using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    /// <summary>
    /// The kinds of hub connection the API makes, sent as <c>spec.connection_type</c>
    /// through <see cref="CreateClientIdentityOptions.ConnectionType"/>. The kind
    /// decides what the connection may send and receive.
    /// </summary>
    public static class ThalovantConnectionTypes
    {
        /// <summary>A device a person talks to.</summary>
        public const string VoiceSatellite = "voice_satellite";

        /// <summary>A chat window on a web page.</summary>
        public const string WebChat = "web_chat";

        /// <summary>A developer's own tooling.</summary>
        public const string Developer = "developer";

        /// <summary>A microcontroller or appliance.</summary>
        public const string Embedded = "embedded";

        /// <summary>
        /// A Home Assistant link: the hub asks it to act on the home
        /// (<see cref="ThalovantHome"/>). A hub takes one.
        /// </summary>
        public const string HomeAssistant = "home_assistant";
    }

    /// <summary>The <c>response_type</c> values a <c>thalovant.home.response</c> may carry.</summary>
    public static class HomeResponseTypes
    {
        /// <summary>The home did what was asked.</summary>
        public const string ActionDone = "action_done";

        /// <summary>The home answered a question.</summary>
        public const string QueryAnswer = "query_answer";

        /// <summary>The home could not; <see cref="HomeAnswer.ErrorCode"/> says why.</summary>
        public const string Error = "error";

        /// <summary>Every value, in the contract's order.</summary>
        public static IReadOnlyList<string> All { get; } = new[] { ActionDone, QueryAnswer, Error };
    }

    /// <summary>The <c>error_code</c> values a <c>thalovant.home.response</c> may carry, only with <see cref="HomeResponseTypes.Error"/>.</summary>
    public static class HomeErrorCodes
    {
        /// <summary>Nothing in the home matched what was said.</summary>
        public const string NoIntentMatch = "no_intent_match";

        /// <summary>It matched, but no device could do it.</summary>
        public const string NoValidTargets = "no_valid_targets";

        /// <summary>The handler failed; the SDK answers this when it throws.</summary>
        public const string FailedToHandle = "failed_to_handle";

        /// <summary>Anything else; the SDK answers this for an answer outside the contract.</summary>
        public const string Unknown = "unknown";

        /// <summary>The handler did not answer in time; the SDK answers this for it.</summary>
        public const string Timeout = "timeout";

        /// <summary>The home's conversation agent is not available.</summary>
        public const string AgentUnavailable = "agent_unavailable";

        /// <summary>Every value, in the contract's order.</summary>
        public static IReadOnlyList<string> All { get; } =
            new[] { NoIntentMatch, NoValidTargets, FailedToHandle, Unknown, Timeout, AgentUnavailable };
    }

    /// <summary>One <c>thalovant.home.request</c>: what was said, in which language.</summary>
    public sealed class HomeRequest
    {
        /// <summary>The hub's id for this request; empty when the hub sent none.</summary>
        public string RequestId { get; }

        /// <summary>What the person said; empty when the hub sent nothing.</summary>
        public string Utterance { get; }

        /// <summary>The language it was said in, such as <c>en-US</c>, or null.</summary>
        public string? Lang { get; }

        /// <summary>The conversation this continues, or null.</summary>
        public string? ConversationId { get; }

        /// <summary>The event the request arrived as, which the answer replies to; null for a request built in code.</summary>
        public ThalovantEvent? Event { get; }

        public HomeRequest(string requestId, string utterance, string? lang = null, string? conversationId = null)
            : this(requestId, utterance, lang, conversationId, null)
        {
        }

        private HomeRequest(string requestId, string utterance, string? lang, string? conversationId, ThalovantEvent? busEvent)
        {
            RequestId = requestId ?? "";
            Utterance = utterance ?? "";
            Lang = lang;
            ConversationId = conversationId;
            Event = busEvent;
        }

        /// <summary>Reads a request out of the event it arrived as.</summary>
        public static HomeRequest FromEvent(ThalovantEvent busEvent)
        {
            if (busEvent is null) throw new ArgumentNullException(nameof(busEvent));
            var data = busEvent.Data;
            return new HomeRequest(
                Text(data["request_id"]) ?? "",
                Text(data["utterance"]) ?? "",
                Text(data["lang"]),
                Text(data["conversation_id"]),
                busEvent);
        }

        /// <summary>A non-empty string, exactly as sent; anything else is absent.</summary>
        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
    }

    /// <summary>
    /// What a handler says back to a <see cref="HomeRequest"/>. <see cref="Speech"/>
    /// may carry markup; it is sent as plain text.
    /// </summary>
    public sealed class HomeAnswer
    {
        /// <summary>What the device should say. Markup is removed before it is sent.</summary>
        public string Speech { get; }

        /// <summary>One of <see cref="HomeResponseTypes"/>; anything else is answered as <see cref="HomeErrorCodes.Unknown"/>.</summary>
        public string ResponseType { get; }

        /// <summary>One of <see cref="HomeErrorCodes"/>, only with <see cref="HomeResponseTypes.Error"/>.</summary>
        public string? ErrorCode { get; }

        /// <summary>Whether the home expects the person to say more.</summary>
        public bool ContinueConversation { get; }

        /// <summary>The conversation to continue; the request's is echoed when this is null.</summary>
        public string? ConversationId { get; }

        public HomeAnswer(
            string speech = "",
            string responseType = HomeResponseTypes.ActionDone,
            string? errorCode = null,
            bool continueConversation = false,
            string? conversationId = null)
        {
            Speech = speech ?? "";
            ResponseType = responseType ?? "";
            ErrorCode = errorCode;
            ContinueConversation = continueConversation;
            ConversationId = conversationId;
        }

        /// <summary>The home did what was asked, and says so.</summary>
        public static HomeAnswer ActionDone(string speech, bool continueConversation = false) =>
            new HomeAnswer(speech, HomeResponseTypes.ActionDone, null, continueConversation);

        /// <summary>The home answers a question.</summary>
        public static HomeAnswer QueryAnswer(string speech, bool continueConversation = false) =>
            new HomeAnswer(speech, HomeResponseTypes.QueryAnswer, null, continueConversation);

        /// <summary>
        /// The home could not. Leave <paramref name="speech"/> empty to let the hub
        /// speak its own sentence for the code, in the device's language.
        /// </summary>
        public static HomeAnswer Error(string errorCode, string speech = "") =>
            new HomeAnswer(speech, HomeResponseTypes.Error, errorCode);
    }

    /// <summary>
    /// Answers one <see cref="HomeRequest"/>. The token is cancelled when the
    /// answer is no longer wanted: the handler ran out of time, or its
    /// subscription was closed.
    /// </summary>
    public delegate ValueTask<HomeAnswer> HomeRequestHandler(HomeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The Home Assistant link: a hub asks a home, and the home always answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A home skill on the hub sends <c>thalovant.home.request</c> to the
    /// account's Home Assistant connection, which hands the utterance to its
    /// conversation agent and answers with <c>thalovant.home.response</c>. Every
    /// request gets exactly one answer, within the hub's 10 seconds; the answer is
    /// a reply (OVOS-MSG-1 §5.2), so it goes back the way the request came;
    /// <c>speech</c> is plain text, never markup; and <c>response_type</c> and
    /// <c>error_code</c> come from <see cref="HomeResponseTypes"/> and
    /// <see cref="HomeErrorCodes"/>.
    /// </para>
    /// <para>
    /// When the SDK has to answer for a handler -- it threw, it was too slow, it
    /// answered outside the contract -- the speech is empty: the hub speaks its
    /// own sentence for the code, in the device's language, which the SDK does
    /// not know. Use <see cref="ThalovantClient.AnswerHomeRequests"/> or
    /// <see cref="HubSession.AnswerHomeRequests"/> to answer every request a
    /// connection receives.
    /// </para>
    /// </remarks>
    public static class ThalovantHome
    {
        /// <summary>The event a hub sends to ask the home.</summary>
        public const string RequestEvent = "thalovant.home.request";

        /// <summary>The event that answers it.</summary>
        public const string ResponseEvent = "thalovant.home.response";

        /// <summary>The hub treats silence after this long as <see cref="HomeErrorCodes.Timeout"/>.</summary>
        public static readonly TimeSpan HubTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a handler has by default: a second inside
        /// <see cref="HubTimeout"/>, so the SDK's own <c>timeout</c> answer still
        /// lands before the hub gives up.
        /// </summary>
        public static readonly TimeSpan DefaultHandlerTimeout = TimeSpan.FromSeconds(9);

        /// <summary>
        /// The scopes a Home Assistant link signs in with
        /// (<see cref="ThalovantControlPlane.BeginDeviceLoginAsync"/>), and all a
        /// Free plan can approve.
        /// </summary>
        public static IReadOnlyList<string> HomeAssistantScopes { get; } = new[] { "hubs:read", "clients:read", "clients:write" };

        private static readonly Regex Markup = new Regex("<[^>]*>", RegexOptions.CultureInvariant);

        /// <summary>
        /// Speech a device can say as it is: markup removed, entities decoded,
        /// whitespace collapsed to single spaces and trimmed.
        /// </summary>
        public static string PlainSpeech(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var decoded = WebUtility.HtmlDecode(Markup.Replace(text, ""));
            var plain = new StringBuilder(decoded.Length);
            var pendingSpace = false;
            foreach (var character in decoded)
            {
                if (IsSpace(character))
                {
                    pendingSpace = plain.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    plain.Append(' ');
                    pendingSpace = false;
                }
                plain.Append(character);
            }
            return plain.ToString();
        }

        /// <summary>
        /// Whitespace as every SDK reads it: .NET's, plus the four information
        /// separators U+001C..U+001F, which Python's <c>str.isspace</c> counts and
        /// <see cref="char.IsWhiteSpace(char)"/> does not.
        /// </summary>
        private static bool IsSpace(char character) =>
            char.IsWhiteSpace(character) || (character >= '\u001C' && character <= '\u001F');

        /// <summary>
        /// The <c>thalovant.home.response</c> payload for <paramref name="answer"/>,
        /// held to the contract.
        /// </summary>
        /// <remarks>
        /// No answer at all, or one outside the contract -- an unknown
        /// <c>response_type</c>, or an <c>error</c> without a known
        /// <c>error_code</c> -- becomes <c>error</c> / <c>unknown</c>, keeping its
        /// speech. <c>error_code</c> is sent only with <c>error</c>, and the
        /// request's <c>conversation_id</c> is echoed when the answer names none.
        /// </remarks>
        public static JsonObject Response(HomeRequest request, HomeAnswer? answer)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            answer ??= HomeAnswer.Error(HomeErrorCodes.Unknown);
            var responseType = answer.ResponseType;
            var errorCode = responseType == HomeResponseTypes.Error ? answer.ErrorCode : null;
            if (!Contains(HomeResponseTypes.All, responseType)
                || (responseType == HomeResponseTypes.Error && !Contains(HomeErrorCodes.All, errorCode)))
            {
                responseType = HomeResponseTypes.Error;
                errorCode = HomeErrorCodes.Unknown;
            }
            var payload = new JsonObject
            {
                ["request_id"] = request.RequestId,
                ["speech"] = PlainSpeech(answer.Speech),
                ["response_type"] = responseType,
                ["continue_conversation"] = answer.ContinueConversation,
            };
            if (!string.IsNullOrEmpty(errorCode))
            {
                payload["error_code"] = errorCode;
            }
            var conversationId = string.IsNullOrEmpty(answer.ConversationId) ? request.ConversationId : answer.ConversationId;
            if (!string.IsNullOrEmpty(conversationId))
            {
                payload["conversation_id"] = conversationId;
            }
            return payload;
        }

        private static bool Contains(IReadOnlyList<string> values, string? value)
        {
            foreach (var candidate in values)
            {
                if (string.Equals(candidate, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Runs <paramref name="handler"/> for one request and returns the payload
        /// to answer it with, whatever the handler did. It does not send it.
        /// </summary>
        /// <remarks>
        /// A handler that throws is answered <see cref="HomeErrorCodes.FailedToHandle"/>;
        /// one that has not answered within <paramref name="timeout"/>
        /// (<see cref="DefaultHandlerTimeout"/>) is answered
        /// <see cref="HomeErrorCodes.Timeout"/>, and its token is cancelled. Only
        /// <paramref name="cancellationToken"/> ends this without a payload.
        /// </remarks>
        public static async Task<JsonObject> AnswerAsync(
            HomeRequest request,
            HomeRequestHandler handler,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            var bound = HandlerTimeout(timeout);
            HomeAnswer? answer;
            using var handlerToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var pending = handler(request, handlerToken.Token);
                if (pending.IsCompleted)
                {
                    answer = await pending.ConfigureAwait(false);
                }
                else
                {
                    var work = pending.AsTask();
                    using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var first = await Task.WhenAny(work, Task.Delay(bound, timer.Token)).ConfigureAwait(false);
                    if (first == work)
                    {
                        timer.Cancel();
                        answer = await work.ConfigureAwait(false);
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        handlerToken.Cancel();
                        // Whatever it does after this is nobody's answer, and a
                        // fault it raises later must not surface as unobserved.
                        _ = work.ContinueWith(
                            finished => _ = finished.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        answer = HomeAnswer.Error(HomeErrorCodes.Timeout);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                answer = HomeAnswer.Error(HomeErrorCodes.FailedToHandle);
            }
            return Response(request, answer);
        }

        private static TimeSpan HandlerTimeout(TimeSpan? timeout)
        {
            var bound = timeout ?? DefaultHandlerTimeout;
            if (bound <= TimeSpan.Zero || bound.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "A home handler's timeout must be positive and fit Task.Delay.");
            }
            return bound;
        }

        /// <summary>
        /// Answers every <see cref="RequestEvent"/> that <paramref name="on"/>
        /// delivers, each on a task of its own so a slow one does not hold up the
        /// next. Closing the subscription cancels the answers still running.
        /// </summary>
        internal static ThalovantSubscription AnswerEvery(
            Func<string, Action<ThalovantEvent>, ThalovantSubscription> on,
            Func<ThalovantEvent, JsonObject, CancellationToken, Task> reply,
            HomeRequestHandler handler,
            TimeSpan? timeout)
        {
            if (handler is null) throw new ArgumentNullException(nameof(handler));
            var bound = HandlerTimeout(timeout);
            var stop = new CancellationTokenSource();
            var token = stop.Token;
            var subscription = on(RequestEvent, busEvent =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                // Off the receive loop: the handler may take seconds.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = await AnswerAsync(HomeRequest.FromEvent(busEvent), handler, bound, token).ConfigureAwait(false);
                        await reply(busEvent, payload, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Closed: nobody is answering on this subscription any more.
                    }
                    catch (Exception)
                    {
                        // The answer could not be sent -- the link dropped. The
                        // hub answers the device with its own timeout.
                    }
                }, CancellationToken.None);
            });
            return new ThalovantSubscription(() =>
            {
                subscription.Close();
                stop.Cancel();
            });
        }
    }

    public static partial class ThalovantContext
    {
        /// <summary>
        /// The context of a reply to a message that carried <paramref name="context"/>
        /// (OVOS-MSG-1 §5.2).
        /// </summary>
        /// <remarks>
        /// A deep copy, so the reply keeps the request's session, its request id
        /// and everything else it said, with the routing turned round: the reply
        /// goes to whoever sent the request (<c>destination</c> becomes the old
        /// <c>source</c>) and comes from whoever it was sent to (<c>source</c>
        /// becomes the old <c>destination</c>, its first entry when that is a
        /// list). A context with neither keeps neither. Read the context as the
        /// hub sent it: <see cref="ThalovantEvent.Context"/> is exactly that.
        /// </remarks>
        public static JsonObject ReplyContext(JsonObject? context)
        {
            var swapped = JsonUtil.CloneObject(context);
            var source = swapped.TryGetPropertyValue("source", out var from) ? from : null;
            var destination = swapped.TryGetPropertyValue("destination", out var to) ? to : null;
            if (destination is not null)
            {
                var first = destination is JsonArray list && list.Count > 0 ? list[0] : destination;
                swapped["source"] = first?.DeepClone();
            }
            if (source is not null)
            {
                swapped["destination"] = source.DeepClone();
            }
            return swapped;
        }
    }
}
