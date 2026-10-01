using CefSharp;
using CefSharp.Structs;

namespace JizuraLive;

public class CAudioHandler : IAudioHandler
{
    public void Dispose()
    {
    }

    public bool GetAudioParameters(IWebBrowser chromiumWebBrowser, IBrowser browser, ref AudioParameters parameters)
    {
        return true;
    }

    public void OnAudioStreamStarted(IWebBrowser chromiumWebBrowser, IBrowser browser, AudioParameters parameters,
        int channels)
    {
    }

    public void OnAudioStreamPacket(IWebBrowser chromiumWebBrowser, IBrowser browser, IntPtr data, int noOfFrames,
        long pts)
    {
    }

    public void OnAudioStreamStopped(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
    }

    public void OnAudioStreamError(IWebBrowser chromiumWebBrowser, IBrowser browser, string errorMessage)
    {
    }
}