FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/InvoiceApi/InvoiceApi.csproj ./
RUN dotnet restore

COPY src/InvoiceApi/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ---

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Fonts for QuestPDF/SkiaSharp — the base image ships none, which renders
# text-less PDFs. fontconfig aliases the requested "Arial" to Liberation Sans.
# Base is Ubuntu 24.04 since .NET 10 (Debian variants discontinued); both
# packages exist under the same names in the Ubuntu repos.
RUN apt-get update \
 && apt-get install -y --no-install-recommends fontconfig fonts-liberation \
 && rm -rf /var/lib/apt/lists/*

# non-root user — the .NET images ship a built-in unprivileged "app" user
# (since .NET 8); the Ubuntu-based 10.0 image no longer includes adduser.
USER app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "InvoiceApi.dll"]
