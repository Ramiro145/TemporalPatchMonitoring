import { Route, Routes } from 'react-router'
import Dashboard from '@/routes/Dashboard'

function App() {
  return (
    <Routes>
      <Route path="/" element={<Dashboard />} />
    </Routes>
  )
}

export default App
