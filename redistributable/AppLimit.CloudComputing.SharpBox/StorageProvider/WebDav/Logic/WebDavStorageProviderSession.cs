using AppLimit.CloudComputing.SharpBox.StorageProvider.API;

namespace AppLimit.CloudComputing.SharpBox.StorageProvider.WebDav.Logic
{
    internal class WebDavStorageProviderSession : IStorageProviderSession
    {
        public WebDavStorageProviderSession(ICloudStorageAccessToken token, WebDavConfiguration config, IStorageProviderService service)
        {
            SessionToken = token;
            ServiceConfiguration = config;
            Service = service;
        }

        #region IStorageProviderSession Members

        public ICloudStorageAccessToken SessionToken { get; private set; }

        public IStorageProviderService Service { get; private set; }

        public ICloudStorageConfiguration ServiceConfiguration { get; private set; }

        #endregion
    }
}