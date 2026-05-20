FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Cinema.Api/Cinema.Api.csproj", "Cinema.Api/"]
COPY ["Cinema.Application/Cinema.Application.csproj", "Cinema.Application/"]
COPY ["Cinema.Domain/Cinema.Domain.csproj", "Cinema.Domain/"]
COPY ["Cinema.Infrastructure/Cinema.Infrastructure.csproj", "Cinema.Infrastructure/"]


RUN dotnet restore "Cinema.Api/Cinema.Api.csproj"

COPY . .

WORKDIR "/src/Cinema.Api"
RUN dotnet publish "Cinema.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "Cinema.Api.dll"]
