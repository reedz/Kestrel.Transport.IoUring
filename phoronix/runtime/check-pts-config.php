<?php
/*
 * Checks that the Phoronix Test Suite configuration PTS will actually read keeps this profile local-only.
 *
 *   php check-pts-config.php [--file PATH]
 *
 * Prints USER_PATH=, CONFIG= and zero or more PROBLEM= lines. Exit status: 0 = no problems, 1 = problems found,
 * 2 = bad usage. Anything other than 0 must be treated as "not safe": PTS silently falls back to defaults that
 * ENABLE result uploads and usage reporting when it cannot parse user-config.xml (a double hyphen inside an XML
 * comment is enough), so grepping the file for FALSE is not a check.
 *
 * The file is chosen the way PTS chooses it (pts-core.php and pts_config::get_config_file_location in PTS 10.8):
 *   PTS_USER_PATH_OVERRIDE when it is an existing directory (appended to literally, without adding a slash),
 *   otherwise <home of the POSIX account>/.phoronix-test-suite/, and /etc/phoronix-test-suite.xml for a privileged
 *   daemon-style install. --file checks an explicit file instead (used to vet the shipped config).
 */

function usage_error(string $message): void
{
    fwrite(STDERR, $message . "\n");
    exit(2);
}

$explicit = null;
for ($i = 1; $i < $argc; $i++) {
    if ($argv[$i] === '--file' && isset($argv[$i + 1])) {
        $explicit = $argv[++$i];
    } else {
        usage_error('unknown argument: ' . $argv[$i]);
    }
}

function pts_home(): string
{
    $home = null;
    if (function_exists('posix_getpwuid') && function_exists('posix_getuid')) {
        $info = posix_getpwuid(posix_getuid());
        $home = $info['dir'] ?? null;
    }
    if (!$home) {
        $home = getenv('DEBUG_REAL_HOME') ?: getenv('HOME');
    }
    if (!$home) {
        usage_error('cannot determine the home directory');
    }
    return substr($home, -1) === '/' ? $home : $home . '/';
}

$problems = [];

$override = getenv('PTS_USER_PATH_OVERRIDE');
if ($override !== false && $override !== '' && is_dir($override)) {
    $userPath = $override;
    if (substr($override, -1) !== '/') {
        $problems[] = "PTS_USER_PATH_OVERRIDE must end with '/': PTS appends file names without a separator";
    }
} else {
    $userPath = pts_home() . '.phoronix-test-suite/';
}

$daemon = is_writable('/var/lib/') && is_writable('/etc');
if ($daemon || (is_file('/etc/phoronix-test-suite.xml') && is_writable('/etc/phoronix-test-suite.xml'))) {
    $effective = '/etc/phoronix-test-suite.xml';
} else {
    $effective = $userPath . 'user-config.xml';
}

$target = $explicit ?? $effective;
echo "USER_PATH=$userPath\n";
echo "CONFIG=$target\n";

// Path below PhoronixTestSuite/Options => the only acceptable value.
$required = [
    'OpenBenchmarking/AnonymousUsageReporting' => 'FALSE',
    'OpenBenchmarking/AlwaysUploadSystemLogs' => 'FALSE',
    'OpenBenchmarking/AllowResultUploadsToOpenBenchmarking' => 'FALSE',
    'Testing/AlwaysUploadResultsToOpenBenchmarking' => 'FALSE',
    'BatchMode/UploadResults' => 'FALSE',
    'Networking/NoNetworkCommunication' => 'TRUE',
    'Modules/AutoLoadModules' => '',
];

if (!is_file($target) || !is_readable($target)) {
    $problems[] = "$target: not found or unreadable (PTS would generate defaults that enable uploads)";
} else {
    libxml_use_internal_errors(true);
    $doc = new DOMDocument();
    if (!$doc->load($target)) {
        $first = libxml_get_errors()[0] ?? null;
        $problems[] = "$target: not well-formed XML" . ($first ? ' (line ' . $first->line . ': ' . trim($first->message) . ')' : '')
            . '; PTS would discard it and fall back to defaults that enable uploads';
    } else {
        $xpath = new DOMXPath($doc);
        foreach ($required as $path => $expected) {
            $nodes = $xpath->query('/PhoronixTestSuite/Options/' . $path);
            if ($nodes === false || $nodes->length !== 1) {
                $problems[] = "$target: $path must appear exactly once (found " . ($nodes ? $nodes->length : 0) . ')';
                continue;
            }
            $value = trim($nodes->item(0)->textContent);
            if ($value !== $expected) {
                $problems[] = "$target: $path is '$value', it must be '$expected'";
            }
        }
    }
}

foreach ($problems as $problem) {
    echo 'PROBLEM=' . str_replace("\n", ' ', $problem) . "\n";
}
exit($problems ? 1 : 0);
