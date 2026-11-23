FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/SmartExpenseTracker.Api -c Release -o /publish

FROM build AS migrate
RUN dotnet tool install dotnet-ef --tool-path /tools --version 10.0.9
ENTRYPOINT ["/tools/dotnet-ef", "database", "update", "--project", "src/SmartExpenseTracker.Persistence", "--startup-project", "src/SmartExpenseTracker.Api", "--configuration", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /publish/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartExpenseTracker.Api.dll"]
