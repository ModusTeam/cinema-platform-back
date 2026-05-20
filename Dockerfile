FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["CinemaApi/CinemaApi.csproj", "CinemaApi/"]
RUN dotnet restore "CinemaApi/CinemaApi.csproj"

COPY . .
WORKDIR "/src/CinemaApi"
RUN dotnet publish "CinemaApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "CinemaApi.dll"]
