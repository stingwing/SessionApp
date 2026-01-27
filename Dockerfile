# Multi-stage Dockerfile for a .NET 10 ASP.NET Core app (multi-stage build)
# Builds with the SDK image and produces a small runtime image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy everything and restore / publish.
# If you have a solution or project layout that places the .csproj in a subfolder,
# adjust the COPY and dotnet publish paths to point to the project file.
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

# Update the DLL name if your project assembly differs.
ENTRYPOINT ["dotnet", "SessionApp.dll"]

# Build the Docker image
# cd "C:\Users\tmollenhauer\source\repos\SessionApp"
# dir
# docker build -t sessionapp:latest .
# Example: Dockerfile lives in a subfolder
docker build -f ./src/MyApp/Dockerfile -t sessionapp:latest .