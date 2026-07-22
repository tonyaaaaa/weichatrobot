param(
    [Parameter(Mandatory = $true)]
    [string]$ObjectRoot,
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [int]$Port = 5591,
    [int]$EmbeddingDimension = 8
)

$resolvedRoot = [System.IO.Path]::GetFullPath($ObjectRoot)
[System.IO.Directory]::CreateDirectory($resolvedRoot) | Out-Null
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()

function Write-Response($response, [int]$status, [string]$contentType, [byte[]]$bytes) {
    $response.StatusCode = $status
    $response.ContentType = $contentType
    $response.ContentLength64 = $bytes.Length
    if ($bytes.Length -gt 0) { $response.OutputStream.Write($bytes, 0, $bytes.Length) }
    $response.Close()
}

function Resolve-ObjectPath([string]$urlPath) {
    $relative = [System.Uri]::UnescapeDataString($urlPath.Substring('/objects/'.Length)).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $candidate = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($resolvedRoot, $relative))
    if (-not $candidate.StartsWith($resolvedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Object path escaped the fake storage root.'
    }
    return $candidate
}

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        try {
            $request = $context.Request
            $path = $request.Url.AbsolutePath
            if ($path.StartsWith('/objects/', [System.StringComparison]::Ordinal)) {
                $target = Resolve-ObjectPath $path
                if ($request.HttpMethod -eq 'PUT') {
                    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
                    $output = [System.IO.File]::Create($target)
                    try { $request.InputStream.CopyTo($output) } finally { $output.Dispose() }
                    [System.IO.File]::AppendAllText($LogPath, ("{0} storage.put {1} bytes={2}`n" -f [DateTime]::UtcNow.ToString('O'), $path, (Get-Item $target).Length))
                    Write-Response $context.Response 200 'application/json' ([System.Text.Encoding]::UTF8.GetBytes('{"ok":true}'))
                } elseif ($request.HttpMethod -eq 'GET' -and [System.IO.File]::Exists($target)) {
                    $bytes = [System.IO.File]::ReadAllBytes($target)
                    [System.IO.File]::AppendAllText($LogPath, ("{0} storage.get {1} bytes={2}`n" -f [DateTime]::UtcNow.ToString('O'), $path, $bytes.Length))
                    Write-Response $context.Response 200 'application/octet-stream' $bytes
                } elseif ($request.HttpMethod -eq 'DELETE') {
                    if ([System.IO.File]::Exists($target)) { [System.IO.File]::Delete($target) }
                    [System.IO.File]::AppendAllText($LogPath, ("{0} storage.delete {1}`n" -f [DateTime]::UtcNow.ToString('O'), $path))
                    Write-Response $context.Response 200 'application/json' ([System.Text.Encoding]::UTF8.GetBytes('{"ok":true}'))
                } else {
                    Write-Response $context.Response 404 'application/json' ([System.Text.Encoding]::UTF8.GetBytes('{"error":"not-found"}'))
                }
            } elseif ($path -eq '/v1/embeddings' -and $request.HttpMethod -eq 'POST') {
                $reader = [System.IO.StreamReader]::new($request.InputStream, $request.ContentEncoding)
                try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $payload = $body | ConvertFrom-Json
                $inputs = @($payload.input)
                $data = @()
                for ($index = 0; $index -lt $inputs.Count; $index++) {
                    $sha = [System.Security.Cryptography.SHA256]::Create()
                    try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes([string]$inputs[$index])) } finally { $sha.Dispose() }
                    $vector = @()
                    for ($position = 0; $position -lt $EmbeddingDimension; $position++) { $vector += (($hash[$position] + 1) / 256.0) }
                    $data += @{ index = $index; embedding = $vector; object = 'embedding' }
                }
                [System.IO.File]::AppendAllText($LogPath, ("{0} embeddings count={1} model={2}`n" -f [DateTime]::UtcNow.ToString('O'), $inputs.Count, $payload.model))
                $bytes = [System.Text.Encoding]::UTF8.GetBytes((@{ object = 'list'; data = $data; model = $payload.model } | ConvertTo-Json -Depth 8 -Compress))
                Write-Response $context.Response 200 'application/json' $bytes
            } elseif ($path -eq '/v1/ocr/pages' -and $request.HttpMethod -eq 'POST') {
                [System.IO.File]::AppendAllText($LogPath, ("{0} ocr.unexpected-request`n" -f [DateTime]::UtcNow.ToString('O')))
                Write-Response $context.Response 500 'application/json' ([System.Text.Encoding]::UTF8.GetBytes('{"error":"OCR was not expected for text fixtures"}'))
            } else {
                Write-Response $context.Response 404 'application/json' ([System.Text.Encoding]::UTF8.GetBytes('{"error":"not-found"}'))
            }
        } catch {
            [System.IO.File]::AppendAllText($LogPath, ("{0} error {1}`n" -f [DateTime]::UtcNow.ToString('O'), $_.Exception.Message))
            if ($context.Response.OutputStream.CanWrite) {
                Write-Response $context.Response 500 'application/json' ([System.Text.Encoding]::UTF8.GetBytes('{"error":"fake-provider-failure"}'))
            }
        }
    }
} finally {
    $listener.Close()
}
