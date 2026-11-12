namespace CoolhandLabs.Http;

/// <summary>
/// A <see cref="DelegatingHandler"/> that logs the LLM API calls passing through it to Coolhand and
/// otherwise leaves them untouched: the .NET counterpart of coolhand-node's interception. Put it in any
/// <see cref="HttpClient"/> pipeline (<see cref="Coolhand.CreateHttpClient"/>, <c>IHttpClientFactory</c>
/// via <c>AddCoolhand()</c>, or the Anthropic SDK's <c>ClientOptions.Handlers</c>).
/// </summary>
/// <remarks>
/// For a captured call the request body is copied as it is sent (never buffered or re-read) and the
/// declared context is snapshotted when the request starts. The response body is copied as the caller
/// reads it, whether it buffers the response or streams it; the call is logged once the body ends, or,
/// if the caller stops early, with what arrived so far (streamed bodies, marked
/// <c>stream_incomplete</c>) or after reading the rest in the background (other bodies). Requests that
/// are not LLM calls pass straight through, and any failure inside capture degrades to a pass-through.
/// </remarks>
public sealed class CoolhandHandler : DelegatingHandler
{
    private readonly Coolhand? _coolhand;

    /// <summary>A handler that logs through <see cref="Coolhand.Global"/>, resolved per request (a pass-through while there is none).</summary>
    public CoolhandHandler() { }

    /// <summary>A handler that logs through <paramref name="coolhand"/>.</summary>
    public CoolhandHandler(Coolhand coolhand) => _coolhand = coolhand ?? throw new ArgumentNullException(nameof(coolhand));

    /// <summary>A handler that logs through <paramref name="coolhand"/> (null: the global client) and sends through <paramref name="innerHandler"/>.</summary>
    public CoolhandHandler(Coolhand? coolhand, HttpMessageHandler innerHandler) : base(innerHandler) => _coolhand = coolhand;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var session = TryBegin(request);
        if (session is null) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Safe(() => session.Failed(e));
            throw;
        }
        try
        {
            var headers = response.Headers.Concat(response.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
            var capture = session.Response((int)response.StatusCode, headers);
            if (response.Content is null) capture.Complete();
            else response.Content = new CapturingResponseContent(response.Content, capture);
        }
        catch (Exception e)
        {
            session.Monitor.Log.Info("⚠️ Could not capture the response for call #" + session.Id + ": " + e.Message);
        }
        return response;
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    private CaptureSession? TryBegin(HttpRequestMessage request)
    {
        try
        {
            var client = _coolhand ?? Coolhand.Global;
            if (client is null || request.RequestUri is null || !request.RequestUri.IsAbsoluteUri) return null;
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
            var session = client.Monitor.Begin(request.Method.Method, request.RequestUri.OriginalString, headers);
            if (session is null) return null;
            if (request.Content is not null) request.Content = new CapturingRequestContent(request.Content, session);
            return session;
        }
        catch
        {
            return null;
        }
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // never let logging break the host request
        }
    }
}
