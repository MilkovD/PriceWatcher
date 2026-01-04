# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project files
COPY *.sln .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY src/PriceWatcher.Domain/*.csproj src/PriceWatcher.Domain/
COPY src/PriceWatcher.Infrastructure/*.csproj src/PriceWatcher.Infrastructure/
COPY src/PriceWatcher.Bot/*.csproj src/PriceWatcher.Bot/
COPY src/PriceWatcher.Worker/*.csproj src/PriceWatcher.Worker/
COPY src/PriceWatcher.App/*.csproj src/PriceWatcher.App/
COPY tests/PriceWatcher.Tests/*.csproj tests/PriceWatcher.Tests/

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY . .

# Build and publish
WORKDIR /src/src/PriceWatcher.App
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage with Playwright
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-noble AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd --gid 1000 pricewatcher && \
    useradd --uid 1000 --gid pricewatcher --shell /bin/bash --create-home pricewatcher

# Install .NET 10 runtime (since Playwright image has older .NET)
RUN apt-get update && \
    apt-get install -y --no-install-recommends wget && \
    wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh && \
    chmod +x dotnet-install.sh && \
    ./dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet && \
    rm dotnet-install.sh && \
    apt-get remove -y wget && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app/publish .

# Create data directory and set permissions
RUN mkdir -p /data && chown -R pricewatcher:pricewatcher /data /app

# Switch to non-root user
USER pricewatcher

# Environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV DB_PATH=/data/pricewatcher.db

EXPOSE 8080

ENTRYPOINT ["dotnet", "PriceWatcher.App.dll"]
