# build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.sln .
COPY TestSerilog/*.csproj TestSerilog/
RUN dotnet restore
COPY . .
RUN dotnet publish TestSerilog -c Release -o /app/publish

# runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
VOLUME ["/app/logs"]
ENTRYPOINT ["dotnet", "TestSerilog.dll"]