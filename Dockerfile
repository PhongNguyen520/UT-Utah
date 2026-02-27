FROM mcr.microsoft.com/playwright/dotnet:v1.48.0-jammy AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["UT-Utah/UT-Utah.csproj", "UT-Utah/"]
RUN dotnet restore "UT-Utah/UT-Utah.csproj"
COPY . .
WORKDIR "/src/UT-Utah"
RUN dotnet build "UT-Utah.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "UT-Utah.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "UT-Utah.dll"]
