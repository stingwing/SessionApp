# Multi-stage Dockerfile for a .NET 10 ASP.NET Core app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file(s) first for better restore/cache behavior
COPY ["SessionApp.csproj", "./"]
RUN dotnet restore "./SessionApp.csproj"

# Copy everything and publish
COPY . .
RUN dotnet publish "SessionApp.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENV ASPNETCORE_URLS=http://+:7086
EXPOSE 7086

ENTRYPOINT ["dotnet", "SessionApp.dll"]