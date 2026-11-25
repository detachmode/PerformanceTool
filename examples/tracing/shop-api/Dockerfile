FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY ShopApi.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
ENV URLS=http://+:8080
ENTRYPOINT ["dotnet", "ShopApi.dll"]
