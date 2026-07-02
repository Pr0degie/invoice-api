FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/InvoiceApi/InvoiceApi.csproj ./
RUN dotnet restore

COPY src/InvoiceApi/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ---

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Fonts for QuestPDF/SkiaSharp — the base image ships none, which renders
# text-less PDFs. fontconfig aliases the requested "Arial" to Liberation Sans.
RUN apt-get update \
 && apt-get install -y --no-install-recommends fontconfig fonts-liberation \
 && rm -rf /var/lib/apt/lists/*

# non-root user
RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "InvoiceApi.dll"]
