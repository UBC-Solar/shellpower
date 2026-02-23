import re
import matplotlib.pyplot as plt
from datetime import datetime
from collections import defaultdict

# --- CONFIGURATION ---
LOG_FILE = r"C:\Users\Jonah\Documents\UBC Solar\shellpower\shellpower\outputs\2026-02-23_11h55m48s\optimization_log.txt"
MIN_THRESHOLD_SEC = 0.01

# Mapping the log text pattern to your descriptive labels
PHASES = {
    "ArrayHandler.mutate_adjacent called!": "-",
    "Cell-string map built!": "Cell-String Mapping",
    "Continuity check passed!": "String Continuity Check",
    "String size check passed!": "String Size Check",
    "Moving cell from string": "Cell Movement",
    "Eval Time:": "Shellpower Simulation"
}

def parse_logs(file_path):
    data = defaultdict(list)
    # Regex to extract timestamp and the message
    log_pattern = re.compile(r"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3}) - \w+ - (.*)")
    
    with open(file_path, 'r') as f:
        last_ts = None
        for line in f:
            match = log_pattern.search(line)
            if not match:
                continue
            
            ts_str, message = match.groups()
            ts = datetime.strptime(ts_str, "%Y-%m-%d %H:%M:%S,%f")
            
            # Reset clock at start of iteration to avoid counting gaps between iterations
            if "Starting simulated annealing iteration" in message:
                last_ts = ts
                continue

            matched_label = None
            for pattern, label in PHASES.items():
                if pattern in message:
                    matched_label = label
                    break
            
            if matched_label:
                if last_ts:
                    duration = (ts - last_ts).total_seconds()
                    data[matched_label].append(duration)
                last_ts = ts
                
    return data

def process_and_plot(data):
    # Calculate averages
    averages = {label: sum(times)/len(times) for label, times in data.items()}
    
    # Filter: Remove '-' label and any values < threshold
    filtered_data = {
        label: avg for label, avg in averages.items() 
        if avg >= MIN_THRESHOLD_SEC and label != "-"
    }

    if not filtered_data:
        print("No processes met the >0.01s threshold.")
        return

    # Sort for cleaner visualization
    sorted_items = sorted(filtered_data.items(), key=lambda x: x[1])
    labels = [x[0] for x in sorted_items]
    values = [x[1] for x in sorted_items]

    plt.figure(figsize=(10, 6))
    bars = plt.barh(labels, values, color='teal', alpha=0.8)
    
    plt.xlabel('Average Time (seconds)')
    plt.title('Average Time Spent Per Process (Filtered > 0.01s)')
    plt.grid(axis='x', linestyle='--', alpha=0.6)

    # Add data labels to the ends of bars
    for bar in bars:
        width = bar.get_width()
        plt.text(width + (max(values)*0.02), bar.get_y() + bar.get_height()/2, 
                 f'{width:.3f}s', va='center', fontweight='bold')

    plt.tight_layout()
    plt.show()
    # plt.savefig('process_times.png')
    print("Plot saved as process_times.png")

if __name__ == "__main__":
    # Create the log file if it doesn't exist for testing, then parse
    log_data = parse_logs(LOG_FILE)
    process_and_plot(log_data)