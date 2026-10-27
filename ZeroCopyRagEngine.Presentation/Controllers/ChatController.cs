using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ModernAiAssistant.Application.DTOs;
using ModernAiAssistant.Application.Services;

namespace ModernAiAssistant.Presentation.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChatRetrievalService _chatRetrievalService;

    public ChatController(ChatRetrievalService chatRetrievalService)
    {
        _chatRetrievalService = chatRetrievalService;
    }

    [HttpPost("ask-stream")]
    public async Task AskStream([FromBody] ChatQueryRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var stream = _chatRetrievalService.AskQuestionStreamAsync(request.Query, cancellationToken);

        await foreach (var token in stream)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // SSE format requires data: payload \n\n
            var message = $"data: {token.Replace("\n", "\\n")}\n\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            await Response.Body.WriteAsync(bytes, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }
}
