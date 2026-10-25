<?php
/*
 * Checks that the Phoronix Test Suite configuration and environment PTS will actually use keep this profile
 * local-only.
 *
 *   php check-pts-config.php [--file PATH]
 *
 * Prints USER_PATH=, CONFIG= and zero or more PROBLEM= lines. Exit status: 0 = no problems, 1 = problems found,
 * 2 = bad usage. Anything other than 0 must be treated as "not safe": PTS silently falls back to defaults that
 * ENABLE result uploads and usage reporting when it cannot read a value from user-config.xml (malformed XML, a
 * shadowing or duplicated element, ...), so grepping the file for FALSE is not a check.
 *
 * Faithfulness to PTS 10.8:
 *  - The file is chosen the way PTS chooses it (pts-core.php, pts_config::get_config_file_location):
 *    <POSIX home>/.phoronix-test-suite/user-config.xml, or /var/lib/phoronix-test-suite/ and
 *    /etc/phoronix-test-suite.xml for a privileged daemon-style install. PTS_USER_PATH_OVERRIDE is refused (see below).
 *  - Values are read with the same traversal as nye_XmlReader::getXMLValue: getElementsByTagName() of the first
 *    path step over the whole document, then the FIRST match narrowed step by step, and the value is used only
 *    when exactly one node remains; otherwise PTS uses its built-in default. A shadow <Options> element placed
 *    before the real one, or a nested duplicate leaf, therefore makes PTS ignore the real values, and so does this
 *    checker.
 *  --file checks an explicit file instead (used to vet the shipped config).
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
        // DEBUG_REAL_HOME is exported by PTS to profile scripts, whose $HOME is the install directory.
        $home = getenv('DEBUG_REAL_HOME') ?: getenv('HOME');
    }
    if (!$home) {
        usage_error('cannot determine the home directory');
    }
    return substr($home, -1) === '/' ? $home : $home . '/';
}

/** Mirror of nye_XmlReader::getXMLValue(): null where PTS would use its fallback default. */
function pts_xml_value(DOMDocument $doc, string $path): ?string
{
    $steps = explode('/', $path);
    $narrow = $doc->getElementsByTagName(array_shift($steps));
    foreach ($steps as $step) {
        if ($narrow->length == 0) {
            break;
        }
        $narrow = $narrow->item(0)->getElementsByTagName($step);
    }
    return $narrow->length == 1 ? $narrow->item(0)->nodeValue : null;
}

$problems = [];

$daemon = is_writable('/var/lib/') && is_writable('/etc');
// PTS expands "~/" in its directory settings (installation, cache, results) from the ACCOUNT's home directory, never
// from PTS_USER_PATH_OVERRIDE, so with an override PTS would read user-config.xml from one place and install and store
// results in another. The profile's helpers rely on one coherent root, so any override is refused. This is checked
// before is_dir(): PTS changes its working directory before it reads the variable, so even a relative path that does
// not exist from here can be a real directory to PTS.
$override = getenv('PTS_USER_PATH_OVERRIDE');
if ($override !== false && $override !== '') {
    $problems[] = 'PTS_USER_PATH_OVERRIDE is set; it is not supported (PTS expands the installation, cache and results '
        . 'directories from the account home, not from it, and resolves a relative value after changing directory)';
}
$userPath = $daemon ? '/var/lib/phoronix-test-suite/' : pts_home() . '.phoronix-test-suite/';

if ($daemon || (is_file('/etc/phoronix-test-suite.xml') && is_writable('/etc/phoronix-test-suite.xml'))) {
    $effective = '/etc/phoronix-test-suite.xml';
} else {
    $effective = $userPath . 'user-config.xml';
}

$target = $explicit ?? $effective;
echo "USER_PATH=$userPath\n";
echo "CONFIG=$target\n";

// Path below PhoronixTestSuite/Options => the only acceptable effective value.
$required = [
    'OpenBenchmarking/AnonymousUsageReporting' => 'FALSE',
    'OpenBenchmarking/AlwaysUploadSystemLogs' => 'FALSE',
    'OpenBenchmarking/AllowResultUploadsToOpenBenchmarking' => 'FALSE',
    'Testing/AlwaysUploadResultsToOpenBenchmarking' => 'FALSE',
    'BatchMode/UploadResults' => 'FALSE',
    // With TRUE, PTS ignores the launcher's preset values and runs every menu combination instead.
    'BatchMode/RunAllTestCombinations' => 'FALSE',
    // PTS tests NoInternetCommunication first (an else-if): with it TRUE, NoNetworkCommunication is never applied.
    'Networking/NoInternetCommunication' => 'FALSE',
    'Networking/NoNetworkCommunication' => 'TRUE',
    'Modules/AutoLoadModules' => '',
    // Where PTS installs, caches and stores results; the launcher and setup scripts rely on these locations.
    'Installation/EnvironmentDirectory' => '~/.phoronix-test-suite/installed-tests/',
    'Installation/CacheDirectory' => '~/.phoronix-test-suite/download-cache/',
    'Testing/ResultsDirectory' => '~/.phoronix-test-suite/test-results/',
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
        foreach ($required as $path => $expected) {
            $value = pts_xml_value($doc, 'PhoronixTestSuite/Options/' . $path);
            // Booleans are parsed by filter_var, which ignores surrounding whitespace; directory values are used as
            // written (PTS expands "~/" in them literally), so those are compared untrimmed.
            $isPath = substr($expected, 0, 2) === '~/';
            if ($value === null) {
                $problems[] = "$target: PTS cannot read $path (missing, duplicated or shadowed) and would use its built-in default";
            } elseif (($isPath ? $value : trim($value)) !== $expected) {
                $problems[] = "$target: $path is '" . ($isPath ? $value : trim($value)) . "', it must be '$expected'";
            }
        }
    }
}

// Ambient PTS controls that redirect where files live, load code, add shell prefixes, or change which
// repetitions count. The launcher sets its own FORCE_TIMES_TO_RUN / PRESET_OPTIONS_VALUES / TEST_RESULTS_*.
$forbiddenEnv = [
    'PTS_TEST_INSTALL_ROOT_PATH', 'PTS_DOWNLOAD_CACHE', 'PTS_EXTRA_SYSTEM_LOGS_DIR',
    'TEST_EXEC_PREPEND', 'EXECUTE_BINARY_PREPEND', 'PTS_MODULES', 'PTS_MODULE_SETUP',
    'SKIP_TESTS', 'SKIP_TESTS_HAVING_ARGS', 'SKIP_TESTING_SUBSYSTEMS',
    'TOTAL_LOOP_TIME', 'TOTAL_LOOP_COUNT', 'LIMIT_ELAPSED_TEST_TIME',
    'FORCE_MIN_TIMES_TO_RUN', 'FORCE_MIN_TIMES_TO_RUN_CUTOFF', 'FORCE_ABSOLUTE_MIN_TIMES_TO_RUN',
    'FORCE_TIMES_TO_RUN_MULTIPLE', 'IGNORE_RUNS', 'TEST_TIMEOUT_AFTER', 'PTS_CONCURRENT_TEST_RUNS',
    'NO_FILE_HASH_CHECKS', 'NO_DOWNLOAD_CACHE', 'MONITOR', 'REMOVE_TESTS_ON_COMPLETION', 'REMOVE_TESTS_OLDER_THAN',
];
foreach ($forbiddenEnv as $name) {
    $value = getenv($name);
    if ($value !== false && $value !== '') {
        $problems[] = "environment variable $name is set; it changes where PTS stores files, what it runs or how results are counted, so it is refused";
    }
}

foreach ($problems as $problem) {
    echo 'PROBLEM=' . str_replace("\n", ' ', $problem) . "\n";
}
exit($problems ? 1 : 0);
