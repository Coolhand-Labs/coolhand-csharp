# Reading Step Templates

Step templates are the skills and other recurring steps Coolhand detects in captured agent sessions.
Private API key.

```csharp
Page<JsonNode> steps = await coolhand.ListStepTemplatesAsync(new ListStepTemplatesParams
{
    Search = "deploy", StepType = "tool_step", IncludeMetrics = true, Page = 1, Per = 25,
});
JsonNode step = await coolhand.GetStepTemplateAsync(42);           // metrics on by default
JsonNode bare = await coolhand.GetStepTemplateAsync(42, includeMetrics: false);
```

`IncludeMetrics` on the list is opt-in because the server caps `per` at 25 when it is set. `metrics`
holds run, turn, tool and efficiency figures and `notes`. The id is the integer id and must be a
positive safe integer (`ArgumentException` otherwise).
