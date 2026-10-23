# ---- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/SettleFlow.Core/SettleFlow.Core.csproj src/SettleFlow.Core/
COPY src/SettleFlow.Web/SettleFlow.Web.csproj src/SettleFlow.Web/
RUN dotnet restore src/SettleFlow.Web/SettleFlow.Web.csproj
COPY src/ src/
RUN dotnet publish src/SettleFlow.Web/SettleFlow.Web.csproj -c Release -o /app --no-restore

# ---- runtime (non-root)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
# The app runs as the image's non-root user; give it a writable data folder (local keys/files/SQLite when used).
RUN mkdir -p /app/App_Data && chown -R $APP_UID /app/App_Data
USER $APP_UID
ENTRYPOINT ["dotnet", "Reconza.Web.dll"]
