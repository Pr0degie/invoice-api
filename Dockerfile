FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/InvoiceApi/InvoiceApi.csproj ./
RUN dotnet restore

COPY src/InvoiceApi/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ---

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# non-root user
RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "InvoiceApi.dll"]
