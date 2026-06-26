FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY GapMap.sln ./
COPY src/GapMap.Api/GapMap.Api.csproj src/GapMap.Api/
RUN dotnet restore src/GapMap.Api/GapMap.Api.csproj

COPY . ./
RUN dotnet publish src/GapMap.Api/GapMap.Api.csproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "GapMap.Api.dll"]
