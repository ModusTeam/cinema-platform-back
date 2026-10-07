FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Packages.props", "./"]
COPY ["src/Host/Cinema.Api/Cinema.Api.csproj", "src/Host/Cinema.Api/"]
COPY ["src/Cinema.Application/Cinema.Application.csproj", "src/Cinema.Application/"]
COPY ["src/Cinema.Domain/Cinema.Domain.csproj", "src/Cinema.Domain/"]
COPY ["src/Cinema.Infrastructure/Cinema.Infrastructure.csproj", "src/Cinema.Infrastructure/"]


RUN dotnet restore "src/Host/Cinema.Api/Cinema.Api.csproj"

COPY . .

WORKDIR "/src/src/Host/Cinema.Api"
RUN dotnet publish "Cinema.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "Cinema.Api.dll"]
