# ===============================
# 🔧 Etapa 1: Build
# ===============================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copiar solución
COPY ActivaFest.sln ./

# Copiar proyecto respetando la carpeta
COPY ActivaFest/ActivaFest.csproj ./ActivaFest/

# Restaurar dependencias
RUN dotnet restore ActivaFest/ActivaFest.csproj

# Copiar TODO el código
COPY . .

# Publicar
RUN dotnet publish ActivaFest/ActivaFest.csproj -c Release -o /publish

# ===============================
# 🚀 Etapa 2: Runtime
# ===============================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:80

COPY --from=build /publish .

EXPOSE 80

ENTRYPOINT ["dotnet", "ActivaFest.dll"]
