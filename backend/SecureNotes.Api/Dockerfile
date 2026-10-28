FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY backend/SecureNotes.Api/SecureNotes.Api.csproj backend/SecureNotes.Api/
RUN dotnet restore backend/SecureNotes.Api/SecureNotes.Api.csproj

COPY backend/SecureNotes.Api/ backend/SecureNotes.Api/
RUN dotnet publish backend/SecureNotes.Api/SecureNotes.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "SecureNotes.Api.dll"]
