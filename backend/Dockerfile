FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/FuelUp.Api/FuelUp.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
# no secrets or settings files with credentials in the image: everything comes from the environment
RUN rm -f appsettings.Development.json && mkdir -p /data/files /data/keys && chown -R app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FuelUp.Api.dll"]
