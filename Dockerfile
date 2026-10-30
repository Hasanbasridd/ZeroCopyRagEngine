FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ZeroCopyRagEngine.sln ./
COPY .editorconfig ./
COPY ZeroCopyRagEngine.Domain/ZeroCopyRagEngine.Domain.csproj ZeroCopyRagEngine.Domain/
COPY ZeroCopyRagEngine.Application/ZeroCopyRagEngine.Application.csproj ZeroCopyRagEngine.Application/
COPY ZeroCopyRagEngine.Infrastructure/ZeroCopyRagEngine.Infrastructure.csproj ZeroCopyRagEngine.Infrastructure/
COPY ZeroCopyRagEngine.Presentation/ZeroCopyRagEngine.Presentation.csproj ZeroCopyRagEngine.Presentation/
COPY ZeroCopyRagEngine.Tests/ZeroCopyRagEngine.Tests.csproj ZeroCopyRagEngine.Tests/
COPY ZeroCopyRagEngine.Benchmarks/ZeroCopyRagEngine.Benchmarks.csproj ZeroCopyRagEngine.Benchmarks/

RUN dotnet restore ZeroCopyRagEngine.sln

COPY . .
RUN dotnet publish ZeroCopyRagEngine.Presentation/ZeroCopyRagEngine.Presentation.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
COPY datasets/ ./datasets/

HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "ZeroCopyRagEngine.Presentation.dll"]

