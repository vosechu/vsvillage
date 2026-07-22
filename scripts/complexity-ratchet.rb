#!/usr/bin/env ruby
# frozen_string_literal: true

#
# Complexity ratchet for VSVillage/src: total cyclomatic complexity may only go DOWN. Recomputes the
# sum of every method's CCN (via lizard) each run and compares it to the single number in
# metrics/complexity-baseline. Fails (exit 1) if the total rose; a clean run lowers the baseline to
# match. It reports THAT complexity got worse, not which method — run `lizard VSVillage/src -w` for that.
#
#   scripts/complexity-ratchet.rb           check + ratchet down; exit 1 if the total rose
#   scripts/complexity-ratchet.rb --check    read-only gate (CI); exit 1 if the total rose, never writes
#   scripts/complexity-ratchet.rb --bless    accept the current total as the new baseline
#
# Requires lizard on PATH (pipx install lizard).

ROOT = File.expand_path('..', __dir__)
BASELINE = File.join(ROOT, 'metrics/complexity-baseline')
mode = ARGV[0]
abort 'usage: complexity-ratchet.rb [--check|--bless]' unless [nil, '--check', '--bless'].include?(mode)
abort 'lizard not found — pipx install lizard' unless system('command -v lizard > /dev/null 2>&1')

# Sum column 1 (CCN) across lizard's per-method CSV — one row per function, no header or footer.
total = Dir.chdir(ROOT) { `lizard VSVillage/src --csv 2>/dev/null` }.lines.sum { |row| row.split(',')[1].to_i }
abort 'lizard reported zero complexity — check the install/path' if total.zero?

baseline = File.exist?(BASELINE) ? File.read(BASELINE).to_i : nil

if baseline && total > baseline && mode != '--bless'
  abort "FAIL: total complexity rose #{baseline} -> #{total} (+#{total - baseline}). Simplify, or --bless a " \
        'reviewed increase. (`lizard VSVillage/src -w` lists the worst methods.)'
end

if mode == '--check'
  if baseline && total < baseline
    puts "PASS (check). Total #{total} is under baseline #{baseline} — run without --check to ratchet down."
  else
    puts "PASS (check). Total complexity #{total} (baseline #{baseline || 'unset'})."
  end
else
  File.write(BASELINE, "#{total}\n")
  change = if baseline.nil? then 'set'
           elsif mode == '--bless' then "blessed at #{total}"
           elsif total < baseline then "ratcheted #{baseline} -> #{total}"
           else "held at #{total}"
           end
  puts "Baseline #{change}. Total CCN across VSVillage/src = #{total}."
end
