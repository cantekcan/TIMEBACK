# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Timeback.Domain/Timeback.Domain.csproj src/Timeback.Domain/
COPY src/Timeback.Application/Timeback.Application.csproj src/Timeback.Application/
COPY src/Timeback.Infrastructure/Timeback.Infrastructure.csproj src/Timeback.Infrastructure/
COPY src/Timeback.Api/Timeback.Api.csproj src/Timeback.Api/
RUN dotnet restore src/Timeback.Api/Timeback.Api.csproj

COPY src/ src/
RUN dotnet publish src/Timeback.Api/Timeback.Api.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
USER app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Timeback.Api.dll"]
