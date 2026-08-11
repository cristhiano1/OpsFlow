import { NavLink } from 'react-router-dom'
import './Sidebar.css'

export function Sidebar() {
  return (
    <aside className="sidebar">
      <nav aria-label="Primary navigation">
        <NavLink to="/" end className="sidebar-nav-link">
          Dashboard
        </NavLink>
        <NavLink to="/projects" className="sidebar-nav-link">
          Projects
        </NavLink>
      </nav>
    </aside>
  )
}
