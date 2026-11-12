# Reading the Help Center

The Coolhand help center (the pages at coolhandlabs.com/help and /updates) needs no API key.

```csharp
var helpCenter = new HelpCenterDocsService();
Page<JsonNode> articles = await helpCenter.SearchHelpArticlesAsync(new SearchHelpArticlesParams { Query = "match rate", PerPage = 5 });
JsonNode article = await helpCenter.GetHelpArticleAsync("feedback-api");     // markdown body and toc
IReadOnlyList<JsonNode> sections = await helpCenter.ListHelpCategoriesAsync();
```

`Category` (a section's `slug`) is sent as `q[help_category_slug_eq]`, with the query as
`q[full_text]`; without a category the query is `q`. A blank query is not sent.

Anonymous callers get 30 requests per minute per IP. A key (`ServiceConfig.ApiKey`, else
`COOLHAND_API_KEY`) is sent as `X-API-Key` and lifts the limit; pass `ApiKey = ""` to stay
anonymous. A rejected key is a 401, never an anonymous retry. A `Coolhand` client exposes the same
three methods and sends its own key.
