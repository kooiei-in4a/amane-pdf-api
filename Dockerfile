FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY src/Amane.Pdf.Api/Amane.Pdf.Api.csproj src/Amane.Pdf.Api/
COPY src/Amane.Pdf.Renderer/Amane.Pdf.Renderer.csproj src/Amane.Pdf.Renderer/
COPY src/Amane.Pdf.TextExtractor/*.csproj src/Amane.Pdf.TextExtractor/packages.lock.json src/Amane.Pdf.TextExtractor/
RUN dotnet restore src/Amane.Pdf.Api/Amane.Pdf.Api.csproj
RUN dotnet restore src/Amane.Pdf.Renderer/Amane.Pdf.Renderer.csproj
RUN dotnet restore src/Amane.Pdf.TextExtractor/Amane.Pdf.TextExtractor.csproj --locked-mode

COPY src/Amane.Pdf.Api/ src/Amane.Pdf.Api/
RUN dotnet publish src/Amane.Pdf.Api/Amane.Pdf.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false
COPY src/Amane.Pdf.Renderer/ src/Amane.Pdf.Renderer/
RUN dotnet publish src/Amane.Pdf.Renderer/Amane.Pdf.Renderer.csproj \
    --configuration Release --no-restore --output /app/renderer /p:UseAppHost=false

COPY src/Amane.Pdf.TextExtractor/ src/Amane.Pdf.TextExtractor/
RUN dotnet publish src/Amane.Pdf.TextExtractor/Amane.Pdf.TextExtractor.csproj \
    --configuration Release --no-restore --output /app/text-extractor /p:UseAppHost=false

FROM ubuntu:26.04 AS pdfcpu-tools
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY scripts/install-pdfcpu.sh /tools/scripts/install-pdfcpu.sh
COPY scripts/verify-pdfcpu-modules.py /tools/scripts/verify-pdfcpu-modules.py
COPY third_party/ /tools/third_party/
COPY THIRD_PARTY_NOTICES.md /tools/THIRD_PARTY_NOTICES.md
RUN /tools/scripts/install-pdfcpu.sh /opt/amane-pdf
COPY scripts/install-compose-mincho.sh /tools/scripts/install-compose-mincho.sh
RUN /tools/scripts/install-compose-mincho.sh /opt/amane-pdf
COPY scripts/install-pdfium.sh /tools/scripts/install-pdfium.sh
RUN /tools/scripts/install-pdfium.sh /opt/pdfium

COPY scripts/install-tessdata-best.sh /tools/scripts/install-tessdata-best.sh
RUN sh /tools/scripts/install-tessdata-best.sh /opt/tessdata-best

FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Pdf__PdfcpuPath=/opt/amane-pdf/bin/pdfcpu \
    Pdf__PdfcpuConfigDir=/opt/amane-pdf/config \
    Pdf__DotnetPath=/usr/bin/dotnet \
    Pdf__RendererPath=/opt/amane-pdf/renderer/Amane.Pdf.Renderer.dll \
    Pdf__PdfiumPath=/opt/amane-pdf/lib/libpdfium.so \
    Pdf__RenderFontDir=/opt/amane-pdf/fonts \
    Pdf__RenderDataLimitBytes=402653184

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends qpdf libjpeg-turbo-progs \
        tesseract-ocr tesseract-ocr-jpn tesseract-ocr-jpn-vert tesseract-ocr-osd \
    && rm -f /usr/bin/pebble \
    && test ! -e /usr/bin/pebble && test ! -L /usr/bin/pebble \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=app:app /app/publish ./
COPY --from=pdfcpu-tools /opt/amane-pdf/ /opt/amane-pdf/
COPY --from=pdfcpu-tools /opt/pdfium/ /opt/amane-pdf/
COPY --from=pdfcpu-tools /opt/tessdata-best/ /opt/amane-pdf/tessdata-best/
RUN ln -s /usr/share/tesseract-ocr/*/tessdata/osd.traineddata /opt/amane-pdf/tessdata-best/osd.traineddata \
    && test -r /opt/amane-pdf/tessdata-best/osd.traineddata
COPY --from=build /app/renderer/ /opt/amane-pdf/renderer/
COPY --from=build /app/text-extractor/ /opt/amane-pdf/text-extractor/
COPY third_party/pdfpig/ /opt/amane-pdf/licenses/pdfpig/

USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Amane.Pdf.Api.dll"]
