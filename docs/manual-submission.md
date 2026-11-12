# Manual Submission

`LogRequestAsync` submits a call that did not go through a `CoolhandHandler`, for example one replayed
from your own records. Nothing is redacted for you here: pass headers and bodies already scrubbed.

```csharp
LogResponse? log = await coolhand.LogRequestAsync(new CallData
{
    Url = "https://api.openai.com/v1/chat/completions",
    Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
    RequestBody = JsonNode.Parse(requestJson),
    ResponseBody = JsonNode.Parse(responseJson),
    StatusCode = 200,
    StreamIncomplete = false,
}, collector: "my-cli-1.2.0", metadata: new Dictionary<string, object?> { ["project_path"] = "/srv/app" });
```

The payload is `{"llm_request_log": {"raw_request": ..., "collector": ..., "metadata"?: ..., "declared"?: ...}}`;
the declared context active at the call is sent as `declared`. The collector defaults to
`coolhand-csharp-<version>-manual`. The method never throws for API or network failures and returns
`null` on failure or in dry-run mode; `LogResponse.Warnings` lists anything the server accepted but
did not apply as declared.
