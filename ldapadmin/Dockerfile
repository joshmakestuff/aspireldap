# Build context for the LdapAdmin container (#78). The context is bundled inside the
# JoshMakeStuff.Aspire.Hosting.OpenLdap nupkg (contentFiles/any/any/ldapadmin/) next to the
# pack-time-published Aspire.LdapAdmin.Web output under app/, and Aspire builds the image
# locally via WithLdapAdmin() — no registry-published image, by decision.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# System.DirectoryServices.Protocols P/Invokes libldap; the aspnet base image (Ubuntu) does
# not carry it. Same dependency the package README documents for Linux consumers.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libldap2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY app/ ./

# The aspnet base image listens on 8080 by default (ASPNETCORE_HTTP_PORTS=8080); the hosting
# integration targets that port. Run as the image's non-root app user.
USER $APP_UID

ENTRYPOINT ["dotnet", "Aspire.LdapAdmin.Web.dll"]
