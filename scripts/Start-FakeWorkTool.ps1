param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [int]$Port = 5588
)

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $reader = [System.IO.StreamReader]::new($context.Request.InputStream, $context.Request.ContentEncoding)
        $body = $reader.ReadToEnd()
        $reader.Dispose()
        [System.IO.File]::AppendAllText($LogPath, ("{0} {1} {2}`n{3}`n" -f [DateTime]::UtcNow.ToString('O'), $context.Request.HttpMethod, $context.Request.RawUrl, $body))
        $bytes = [System.Text.Encoding]::UTF8.GetBytes('{"code":0,"message":"fake accepted"}')
        $context.Response.StatusCode = 200
        $context.Response.ContentType = 'application/json'
        $context.Response.ContentLength64 = $bytes.Length
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
    }
}
finally {
    $listener.Close()
}
