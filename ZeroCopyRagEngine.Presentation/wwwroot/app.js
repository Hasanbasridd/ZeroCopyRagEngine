const chatArea = document.getElementById('chat-area');
const queryInput = document.getElementById('query-input');
const sendBtn = document.getElementById('send-btn');

async function askQuestion() {
    const query = queryInput.value.trim();
    if (!query) return;

    queryInput.value = '';
    
    // Add User Message
    const userMsg = document.createElement('div');
    userMsg.className = 'message user-message';
    userMsg.textContent = query;
    chatArea.appendChild(userMsg);
    
    // Add AI Message Container
    const aiMsg = document.createElement('div');
    aiMsg.className = 'message ai-message';
    chatArea.appendChild(aiMsg);
    chatArea.scrollTop = chatArea.scrollHeight;

    try {
        const response = await fetch('/api/chat/ask-stream', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ Query: query })
        });

        const reader = response.body.getReader();
        const decoder = new TextDecoder('utf-8');

        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            
            const chunk = decoder.decode(value, { stream: true });
            const lines = chunk.split('\n');
            for (let line of lines) {
                if (line.startsWith('data: ')) {
                    const text = line.substring(6).replace(/\\n/g, '\n');
                    aiMsg.textContent += text;
                    chatArea.scrollTop = chatArea.scrollHeight;
                }
            }
        }
    } catch (e) {
        aiMsg.textContent += "\n[SYSTEM_ERROR: Connection to Zero-Copy Engine lost]";
    }
}

sendBtn.addEventListener('click', askQuestion);
queryInput.addEventListener('keypress', (e) => {
    if (e.key === 'Enter') askQuestion();
});
