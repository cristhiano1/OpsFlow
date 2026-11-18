import { NavLink } from 'react-router-dom'
import './Sidebar.css'

interface SidebarProps {
  showAnalytics: boolean
}

export function Sidebar({ showAnalytics }: SidebarProps) {
  return (
    <aside className="sidebar">
      <nav aria-label="Primary navigation">
        <NavLink to="/" end className="sidebar-nav-link">
          {showAnalytics ? 'Dashboard' : 'Home'}
        </NavLink>
        <NavLink to="/projects" className="sidebar-nav-link">
          Projects
        </NavLink>
      </nav>
    </aside>
  )
}
