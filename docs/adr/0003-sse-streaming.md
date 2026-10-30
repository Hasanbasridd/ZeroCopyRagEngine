# ADR-0003: SSE streaming over IAsyncEnumerable

- Status: Accepted
- Date: 2026-10-26

## Context
LLM answers are generated token by token. Waiting for the full answer increases time-to-first-token (TTFT) for the client UI.

## Decision
`ChatRetrievalService.AskQuestionStreamAsync` returns `IAsyncEnumerable<string>`. `ChatController` (`POST /api/chat/ask-stream`) writes each token as a Server-Sent Events `data:` frame and flushes it immediately while propagating `HttpContext.RequestAborted` `CancellationToken`.

## Rationale
- `IAsyncEnumerable<string>` lets the Application layer stream without depending on ASP.NET Core HTTP types, keeping Clean Architecture boundaries intact.
- SSE runs over standard HTTP, is supported natively by browsers, and needs no bidirectional WebSocket state for unidirectional token streaming.
- Propagating `CancellationToken` through the async iterator immediately stops LLM token generation when a client disconnects.

## Consequences
- Tokens reach the client as they are produced; the full answer is never buffered server-side.
- Each token still allocates a small `string` and a UTF-8 `byte[]` frame buffer.
