FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Server/Drawmaha.Server.csproj Server/
COPY Assets/Drawmaha/Core Assets/Drawmaha/Core
COPY Assets/Drawmaha/Network/Wire.cs Assets/Drawmaha/Network/Wire.cs
RUN dotnet restore Server/Drawmaha.Server.csproj
COPY Server/*.cs Server/
COPY Server/appsettings.json Server/
RUN dotnet publish Server/Drawmaha.Server.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Drawmaha.Server.dll"]
