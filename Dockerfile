# Use the official ASP.NET runtime as a parent image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

# Use the .NET SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["FileUploadApi.csproj", "./"]
RUN dotnet restore "./FileUploadApi.csproj"

# Copy everything and build the application
COPY . .
RUN dotnet publish "./FileUploadApi.csproj" -c Release -o /app/publish

# Use the built app
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FileUploadApi.dll"]
