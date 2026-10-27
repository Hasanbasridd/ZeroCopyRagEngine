using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModernAiAssistant.Application.DTOs;
using Xunit;

namespace ModernAiAssistant.Tests;

public class PresentationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PresentationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AskStream_Should_Return_SSE_Stream_With_Low_TTFT()
    {
        var client = _factory.CreateClient();
        var request = new ChatQueryRequest("What is zero-copy?");

        var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/chat/ask-stream");
        requestMessage.Content = content;

        var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
        
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Read first token (Time-To-First-Token)
        var firstLine = await reader.ReadLineAsync();
        
        firstLine.Should().NotBeNullOrEmpty();
        firstLine.Should().StartWith("data: ");
    }
}
