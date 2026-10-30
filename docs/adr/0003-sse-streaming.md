# ADR-0003: SSE streaming over IAsyncEnumerable

- Status: Accepted
- Date: 2026-10-26

## Context
LLM answers are generated token by token. Waiting for the full answer increases time-to-first-token for the UI.

## Decision
`ChatRetrievalService.AskQuestionStreamAsync` returns `IAsyncEnumerable<string>`. `ChatController` (`POST /api/chat/ask-stream`) writes each token as a Server-Sent Events `data:` frame and flushes it. The static UI in `wwwroot` consumes the stream. Covered by an integration test (`WebApplicationFactory`) that checks the `text/event-stream` content type.

## Rationale
- `IAsyncEnumerable<string>` lets the Application layer stream without depending on HTTP types, keeping Clean Architecture boundaries intact.
- SSE runs over plain HTTP, is supported natively by browsers and needs no extra protocol or library; WebSockets would add bidirectional complexity that this flow does not need.
- Streaming lowers time-to-first-token compared to returning the full answer at once.

## Consequences
- Tokens reach the client as they are produced; the full answer is not buffered server-side.
- Each token allocates a string and a UTF-8 byte array.
- SSE is one-directional (server → client); a POST body carries the query.
- `CancellationToken` is passed to the service and checked in the write loop.
