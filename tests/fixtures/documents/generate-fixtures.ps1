$ErrorActionPreference = 'Stop'
$fixtureRoot = $PSScriptRoot

[System.Text.Encoding]::RegisterProvider([System.Text.CodePagesEncodingProvider]::Instance)
[System.IO.File]::WriteAllBytes((Join-Path $fixtureRoot 'gb18030.txt'), [System.Text.Encoding]::GetEncoding('GB18030').GetBytes('中文内容'))

$docxPath = Join-Path $fixtureRoot 'headings-table.docx'
$docxStream = [System.IO.File]::Create($docxPath)
$archive = [System.IO.Compression.ZipArchive]::new($docxStream, [System.IO.Compression.ZipArchiveMode]::Create)
$entries = [ordered]@{
    '[Content_Types].xml' = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
    '_rels/.rels' = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
    'word/document.xml' = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>指南</w:t></w:r></w:p><w:p><w:r><w:t>正文</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>名称</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>值</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>'
}
foreach ($pair in $entries.GetEnumerator()) {
    $entry = $archive.CreateEntry($pair.Key, [System.IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $writer = [System.IO.StreamWriter]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false))
    $writer.Write($pair.Value)
    $writer.Dispose()
}
$archive.Dispose()
$docxStream.Dispose()

function Write-Pdf([string]$path, [string[]]$objects) {
    $ascii = [System.Text.Encoding]::ASCII
    $stream = [System.IO.MemoryStream]::new()
    $header = $ascii.GetBytes("%PDF-1.4`n")
    $stream.Write($header)
    $offsets = [System.Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $objects.Count; $index++) {
        $offsets.Add([int]$stream.Position)
        $body = $ascii.GetBytes("$($index + 1) 0 obj`n$($objects[$index])`nendobj`n")
        $stream.Write($body)
    }
    $xref = [int]$stream.Position
    $writer = [System.IO.StreamWriter]::new($stream, $ascii, 1024, $true)
    $writer.NewLine = "`n"
    $writer.Write("xref`n0 $($objects.Count + 1)`n0000000000 65535 f`n")
    foreach ($offset in $offsets) { $writer.Write(('{0:D10} 00000 n' -f $offset) + "`n") }
    $writer.Write("trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($path, $stream.ToArray())
    $writer.Dispose()
    $stream.Dispose()
}

$pageOne = 'BT /F1 12 Tf 72 720 Td (Page one) Tj ET'
$pageTwo = 'BT /F1 12 Tf 72 720 Td (Page two) Tj ET'
Write-Pdf (Join-Path $fixtureRoot 'text-pages.pdf') @(
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>',
    "<< /Length $($pageOne.Length) >>`nstream`n$pageOne`nendstream",
    "<< /Length $($pageTwo.Length) >>`nstream`n$pageTwo`nendstream",
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
)
Write-Pdf (Join-Path $fixtureRoot 'scanned-empty.pdf') @(
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>',
    "<< /Length 0 >>`nstream`n`nendstream"
)
