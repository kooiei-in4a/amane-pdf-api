FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY src/Amane.Pdf.Api/Amane.Pdf.Api.csproj src/Amane.Pdf.Api/
RUN dotnet restore src/Amane.Pdf.Api/Amane.Pdf.Api.csproj

COPY src/Amane.Pdf.Api/ src/Amane.Pdf.Api/
RUN dotnet publish src/Amane.Pdf.Api/Amane.Pdf.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends qpdf \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=app:app /app/publish ./

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Amane.Pdf.Api.dll"]
